using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeaconLog;
using Microsoft.Win32;

internal static class Program
{
    [DllImport("advapi32.dll")]
    private static extern int RegOverridePredefKey(IntPtr predefinedKey, IntPtr replacement);

    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), $"BeaconLog.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var registryPath = $@"Software\BeaconLog.Tests.{Guid.NewGuid():N}";
        using var isolatedRegistry = Registry.CurrentUser.CreateSubKey(registryPath);
        var currentUser = new IntPtr(unchecked((int)0x80000001));
        try
        {
            var error = RegOverridePredefKey(currentUser, isolatedRegistry.Handle.DangerousGetHandle());
            if (error != 0)
            {
                throw new IOException($"Unable to isolate the test registry hive ({error}).");
            }
            TestRecords(root);
            TestBackups(root);
            LayoutChecks.Run();
            if (args.Contains("--benchmark", StringComparer.Ordinal))
            {
                BenchmarkRecords(root);
            }
            Console.WriteLine("PASS: records, backup scopes, rollback, interrupted recovery, automatic retention, journal layout");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            RegOverridePredefKey(currentUser, IntPtr.Zero);
            Registry.CurrentUser.DeleteSubKeyTree(registryPath);
            var backups = Path.Combine(root, "Backups");
            if (Directory.Exists(backups))
            {
                foreach (var file in Directory.EnumerateFiles(backups)) File.Delete(file);
                Directory.Delete(backups);
            }
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    private static JournalEntry Entry(string text) => new(Guid.NewGuid(), text,
        new DateTimeOffset(2026, 1, 1, 23, 59, 0, TimeSpan.FromHours(-8)), "#147258", 126);

    private static void TestRecords(string root)
    {
        var path = Path.Combine(root, "record-tests.json");
        var store = new JournalStore(path);
        var first = Entry("first");
        var edited = Entry("second");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { first }));
        Equal(JournalCategory.Ordinary, store.Load().Entries.Single().RecordCategory, "legacy category");
        var guardRan = false;
        store.CheckMutation = () =>
        {
            Throws<IOException>(() =>
            {
                using var unexpected = store.AcquireWriteLock();
            });
            guardRan = true;
        };

        var category = store.AddCategory("Work").Categories[^1];
        Equal(true, guardRan, "mutation guard runs under the write lock");
        store.Add(edited with { Category = category });
        store.Edit(edited with { Category = category }, "changed", category);
        var records = store.Load().Entries;
        Equal(edited.RecordedAt, records[1].RecordedAt, "edit preserves recorded date");
        Equal(edited.ElapsedSeconds, records[1].ElapsedSeconds, "edit preserves duration");
        Equal(category, records[1].RecordCategory, "edit preserves category");

        using (store.AcquireWriteLock())
        {
            Throws<IOException>(() => store.Delete(first.Id));
            Equal(2, store.Load().Entries.Count, "failed delete retains records");
        }
        store.Delete(first.Id);
        Throws<IOException>(() => store.Delete(first.Id));
        Equal("changed", store.Load().Entries.Single().Text, "delete uses record ID");
    }

    private static void TestBackups(string root)
    {
        var path = Path.Combine(root, "records.json");
        var folder = Path.Combine(root, "Backups");
        var manual = Path.Combine(root, "manual.json");
        var export = Path.Combine(root, "history.json");
        var pending = Path.Combine(folder, "pending-restore.json");
        var store = new JournalStore(path);
        var service = new BackupService(store, folder);
        var first = Entry("in the backup");
        store.Add(first);
        SetDiameter(22);
        StartupSettings.StartAtLogin = true;
        service.SaveManualBackup(manual);
        service.ExportHistory(export);
        Throws<JsonException>(() => BackupService.ReadBackup(export));
        Throws<IOException>(() => service.ExportHistory(path));
        using (var document = JsonDocument.Parse(File.ReadAllText(export)))
        {
            Equal(1, document.RootElement.GetProperty("Entries").GetArrayLength(), "exported records");
            Equal(false, document.RootElement.TryGetProperty("Settings", out _), "history export scope");
        }

        var snapshot = BackupService.ReadBackup(manual);
        var current = Entry("current record");
        store.Add(current);
        SetDiameter(31);

        var before = service.Restore(snapshot, RestoreScope.History);
        Equal(31, BackupPreferences.Capture().Diameter, "history-only preserves settings");
        Equal(first.Id, store.Load().Entries.Single().Id, "history restored");
        Equal(2, BackupService.ReadBackup(before.SafetyCopy).Journal.Entries.Count, "safety copy");
        store.Add(current);
        service.Restore(snapshot, RestoreScope.Settings);
        Equal(2, store.Load().Entries.Count, "settings-only preserves history");
        Equal(22, BackupPreferences.Capture().Diameter, "settings restored");

        SetDiameter(39);
        var login = "previous-program --startup-at-login";
        StartupSettings.SetLoginRegistration(login);
        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Throws<IOException>(() => service.Restore(snapshot, RestoreScope.All));
            Equal(39, BackupPreferences.Capture().Diameter, "failed restore rolls settings back");
            Equal(login, StartupSettings.GetLoginRegistration(), "failed restore rolls login back");
            Equal(2, store.Load().Entries.Count, "failed restore preserves history");
            Equal(false, File.Exists(pending), "successful rollback clears marker");
        }
        File.Delete(path + ".tmp");

        var interruptedCopy = Path.Combine(folder, "before-restore-test.json");
        service.SaveManualBackup(interruptedCopy);
        File.WriteAllText(pending, JsonSerializer.Serialize(new PendingRestore(Path.GetFileName(interruptedCopy), true, login)));
        Throws<IOException>(() => store.Delete(first.Id));
        Equal(2, store.Load().Entries.Count, "pending restore blocks ordinary deletion");
        using (store.AcquireWriteLock()) store.WriteLocked(snapshot.Journal);
        SetDiameter(22);
        StartupSettings.SetLoginRegistration(null);
        service.RecoverInterruptedRestore();
        Equal(2, store.Load().Entries.Count, "interrupted restore rolls history back");
        Equal(39, BackupPreferences.Capture().Diameter, "interrupted restore rolls settings back");
        Equal(login, StartupSettings.GetLoginRegistration(), "interrupted restore rolls login back");
        Equal(false, File.Exists(pending), "startup rollback clears marker");
        Equal<BackupPreferences?>(null, service.RecoverInterruptedRestore(), "startup recovery is idempotent");

        File.WriteAllText(pending, JsonSerializer.Serialize(new PendingRestore(Path.GetFileName(interruptedCopy), false, login)));
        using (store.AcquireWriteLock()) store.WriteLocked(snapshot.Journal);
        SetDiameter(24);
        service.RecoverInterruptedRestore();
        Equal(1, store.Load().Entries.Count, "interrupted settings-only restore preserves history");
        Equal(39, BackupPreferences.Capture().Diameter, "interrupted settings-only rollback");

        service.Restore(snapshot, RestoreScope.All);
        Equal(first.Id, store.Load().Entries.Single().Id, "full restore commits");
        Equal(false, File.Exists(pending), "full restore clears marker");
        var now = new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero);
        service.CheckAutomatic(now);
        service.CheckAutomatic(now.AddDays(1));
        Equal(1, Directory.GetFiles(folder, "automatic-*.json").Length, "unchanged data skips backup");
        for (var day = 1; day < 10; day++)
        {
            SetDiameter(22 + day);
            service.CheckAutomatic(now.AddDays(day));
            SetDiameter(36);
            service.CheckAutomatic(now.AddDays(day));
        }
        Equal(7, Directory.GetFiles(folder, "automatic-*.json").Length, "retention and daily limit");
    }

    private static void BenchmarkRecords(string root)
    {
        foreach (var count in new[] { 1_000, 10_000, 50_000 })
        {
            var path = Path.Combine(root, $"benchmark-{count}.json");
            var store = new JournalStore(path);
            var start = new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.FromHours(8));
            var entries = Enumerable.Range(0, count).Select(index => new JournalEntry(Guid.NewGuid(),
                $"完成第 {index} 项工作 · Review notes and update the journal.", start.AddMinutes(index * 30L),
                "#147258", 125, JournalCategory.Ordinary)).ToList();
            var data = JournalData.Empty();
            data.Entries.AddRange(entries);
            using (store.AcquireWriteLock()) store.WriteLocked(data);

            var load = MedianMilliseconds(() => store.Load());
            var indexTime = MedianMilliseconds(() => entries.ToLookup(entry => entry.RecordedDate));
            var first = entries[0];
            var update = MedianMilliseconds(() => store.Edit(first, first.Text + ".", JournalCategory.Ordinary));
            Console.WriteLine($"{count,6} records | load {load,7:F2} ms | date index {indexTime,7:F2} ms | edit {update,7:F2} ms");
        }
    }

    private static double MedianMilliseconds(Action operation)
    {
        operation();
        var samples = new double[5];
        for (var index = 0; index < samples.Length; index++)
        {
            var clock = Stopwatch.StartNew();
            operation();
            samples[index] = clock.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static void SetDiameter(int diameter)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupSettings.SettingsKey);
        key.SetValue(StatusBallAppearance.DiameterValue, diameter);
    }

    private static void Equal<T>(T expected, T actual, string operation)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"{operation}: expected {expected}, got {actual}");
        }
    }

    private static void Throws<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

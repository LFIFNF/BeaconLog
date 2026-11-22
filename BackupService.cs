using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json;

namespace BeaconLog;

internal enum RestoreScope
{
    History,
    Settings,
    All,
}

internal sealed record FullBackup(string Kind, int Version, DateTimeOffset CreatedAt,
    JournalData Journal, BackupPreferences Settings, byte[]? UnreadableHistory = null);

internal sealed record HistoryExport(string Kind, int Version, DateTimeOffset CreatedAt, List<JournalEntry> Entries);
internal sealed record RestoreResult(JournalData? Journal, BackupPreferences? Settings, string SafetyCopy);

internal sealed class BackupService(JournalStore journal, string folder)
{
    private const string BackupKind = "BeaconLog.Backup";
    private const int FormatVersion = 1;
    private const string AutomaticPrefix = "automatic-";
    private const string RestorePrefix = "before-restore-";
    private const int RetentionCount = 7;
    private static readonly JsonSerializerOptions Serialization = new() { WriteIndented = true };

    internal DateTimeOffset? LastAutomaticBackup { get; private set; }
    internal string? AutomaticError { get; private set; }

    internal void ExportHistory(string path)
    {
        GuardDestination(path);
        var data = journal.Load();
        var entries = data.Entries.Select(entry => entry with { Category = entry.RecordCategory }).ToList();
        Write(path, new HistoryExport("BeaconLog.History", FormatVersion, DateTimeOffset.Now, entries));
    }

    internal void SaveManualBackup(string path)
    {
        GuardDestination(path);
        using var writeLock = journal.AcquireWriteLock();
        Write(path, Capture(DateTimeOffset.Now, journal.Load()));
    }

    internal IReadOnlyList<string> ListLocalBackups() => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*.json")
            .Where(path => IsAutomatic(path) || Path.GetFileName(path).StartsWith(RestorePrefix, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
        : [];

    internal static FullBackup ReadBackup(string path)
    {
        using var stream = File.OpenRead(path);
        return Validate(JsonSerializer.Deserialize<FullBackup>(stream));
    }

    internal RestoreResult Restore(FullBackup backup, RestoreScope scope)
    {
        Validate(backup);
        var restoreHistory = scope is RestoreScope.History or RestoreScope.All;
        var restoreSettings = scope is RestoreScope.Settings or RestoreScope.All;
        if (restoreHistory && backup.UnreadableHistory is not null)
        {
            throw new JsonException("此副本保留的原始历史无法解析，可以选择仅恢复设置。");
        }

        using var writeLock = journal.AcquireWriteLock();
        var now = DateTimeOffset.Now;
        var safetyCopy = Path.Combine(folder,
            $"{RestorePrefix}{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(folder);
        FullBackup previous;
        try
        {
            previous = Capture(now, journal.Load());
        }
        catch (JsonException)
        {
            previous = Capture(now, JournalData.Empty()) with { UnreadableHistory = journal.ReadUnparsed() };
        }
        Write(safetyCopy, previous);

        Action? rollBack = null;
        try
        {
            if (restoreSettings)
            {
                rollBack = backup.Settings.Apply();
            }
            if (restoreHistory)
            {
                journal.WriteLocked(backup.Journal);
            }
        }
        catch (Exception error) when (IsDataError(error))
        {
            try
            {
                rollBack?.Invoke();
            }
            catch (Exception rollbackError) when (IsDataError(rollbackError))
            {
                throw new IOException($"恢复失败，部分设置无法回滚。恢复前副本：{safetyCopy}",
                    new AggregateException(error, rollbackError));
            }

            throw new IOException($"恢复失败。{error.Message}\n恢复前副本：{safetyCopy}", error);
        }

        return new RestoreResult(restoreHistory ? backup.Journal : null,
            restoreSettings ? backup.Settings : null, safetyCopy);
    }

    internal void CheckAutomatic(DateTimeOffset now)
    {
        try
        {
            var snapshots = AutomaticBackups();
            LastAutomaticBackup = snapshots.Length == 0 ? null : new DateTimeOffset(File.GetLastWriteTime(snapshots[0]));
            AutomaticError = null;
            if (!StartupSettings.AutomaticBackup)
            {
                return;
            }

            var target = Path.Combine(folder,
                AutomaticPrefix + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");
            if (File.Exists(target))
            {
                PruneAutomatic(snapshots);
                return;
            }

            using var writeLock = journal.AcquireWriteLock();
            if (File.Exists(target))
            {
                LastAutomaticBackup = new DateTimeOffset(File.GetLastWriteTime(target));
                return;
            }

            var snapshot = Capture(now, journal.Load());
            if (snapshots.Length == 0 && SameData(snapshot, new FullBackup(
                BackupKind, FormatVersion, now, JournalData.Empty(), BackupPreferences.Defaults)))
            {
                return;
            }
            if (snapshots.Length != 0 && TryReadBackup(snapshots[0]) is { } latest && SameData(snapshot, latest))
            {
                return;
            }

            Directory.CreateDirectory(folder);
            Write(target, snapshot, overwrite: false);
            LastAutomaticBackup = now;
            PruneAutomatic(AutomaticBackups());
        }
        catch (Exception error) when (IsDataError(error))
        {
            AutomaticError = "自动备份失败，应用会稍后重试。";
            System.Diagnostics.Trace.TraceWarning("Automatic backup failed: {0}", error.Message);
        }
    }

    private string[] AutomaticBackups() => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, $"{AutomaticPrefix}*.json").Where(IsAutomatic)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray()
        : [];

    private static bool IsAutomatic(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        return file.StartsWith(AutomaticPrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(file[AutomaticPrefix.Length..], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static void PruneAutomatic(IEnumerable<string> newestFirst)
    {
        foreach (var path in newestFirst.Skip(RetentionCount))
        {
            File.Delete(path);
        }
    }

    private static FullBackup? TryReadBackup(string path)
    {
        try
        {
            return ReadBackup(path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SameData(FullBackup current, FullBackup previous) =>
        JsonSerializer.Serialize(current with { CreatedAt = default })
        == JsonSerializer.Serialize(previous with { CreatedAt = default });

    private static FullBackup Validate(FullBackup? backup)
    {
        if (backup is null || backup.Kind != BackupKind || backup.Version != FormatVersion
            || backup.CreatedAt == default || backup.Settings is null)
        {
            throw new JsonException("请选择受支持的 BeaconLog 完整备份。历史导出文件不能用于恢复。");
        }

        JournalStore.Validate(backup.Journal);
        backup.Settings.Validate();
        return backup;
    }

    private static FullBackup Capture(DateTimeOffset now, JournalData data) =>
        new(BackupKind, FormatVersion, now, data, BackupPreferences.Capture());

    private void GuardDestination(string path)
    {
        if (journal.IsBackingPath(path))
        {
            throw new IOException("请选择其他文件名，避免覆盖正在使用的记录文件。");
        }
    }

    private static void Write<T>(string path, T data, bool overwrite = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, Serialization);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (IsDataError(error)) { }
            }
        }
    }

    internal static bool IsDataError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException or JsonException;
}

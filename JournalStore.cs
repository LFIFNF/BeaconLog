using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeaconLog;

internal sealed record JournalCategory(Guid Id, string Name)
{
    internal static JournalCategory Ordinary { get; } = new(Guid.Empty, "普通");

    internal static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name)
        && !name.Any(character => char.IsWhiteSpace(character) || character is '/' or '／');

    internal static JournalCategory? Match(IReadOnlyList<JournalCategory> categories, string command)
    {
        if (command.Length < 2 || command[0] != '/' || !IsValidName(command[1..]))
        {
            return null;
        }

        var prefix = command[1..];
        return categories.FirstOrDefault(category => category.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            ?? categories.FirstOrDefault(category => category.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record JournalEntry(Guid Id, string Text, DateTimeOffset RecordedAt, string ColorHex, long ElapsedSeconds,
    JournalCategory? Category = null)
{
    [JsonIgnore]
    public DateOnly RecordedDate => DateOnly.FromDateTime(RecordedAt.DateTime);

    [JsonIgnore]
    public string ClockText => RecordedAt.ToString("HH:mm", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string DateText => RecordedAt.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string DurationText => $"耗时 {ElapsedSeconds / 60} 分钟";

    [JsonIgnore]
    public JournalCategory RecordCategory => Category ?? JournalCategory.Ordinary;

    [JsonIgnore]
    public string? CategoryLabel => RecordCategory.Id == Guid.Empty ? null : RecordCategory.Name;
}

internal sealed record JournalData(int Version, List<JournalCategory> Categories, List<JournalEntry> Entries)
{
    internal const int CurrentVersion = 1;

    internal static JournalData Empty() => new(CurrentVersion, [JournalCategory.Ordinary], []);
}

internal sealed class JournalStore(string path)
{
    internal static JournalStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeaconLog", "records.json"));

    internal JournalData Load()
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = JsonDocument.Parse(file);
            var root = document.RootElement;
            var data = root.ValueKind == JsonValueKind.Array
                ? new JournalData(JournalData.CurrentVersion, [JournalCategory.Ordinary],
                    root.Deserialize<List<JournalEntry>>()!)
                : root.Deserialize<JournalData>();
            return Validate(data);
        }
        catch (FileNotFoundException)
        {
            return JournalData.Empty();
        }
        catch (DirectoryNotFoundException)
        {
            return JournalData.Empty();
        }
    }

    internal static JournalData Validate(JournalData? data)
    {
        if (data is null || data.Version != JournalData.CurrentVersion
            || data.Categories is not { Count: > 0 } || data.Categories[0] != JournalCategory.Ordinary
            || data.Categories.Any(category => category is null || !JournalCategory.IsValidName(category.Name))
            || data.Categories.Select(category => category.Id).Distinct().Count() != data.Categories.Count
            || data.Categories.Select(category => category.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Categories.Count
            || data.Entries is null || data.Entries.Any(entry => entry is null || entry.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.Text) || entry.ElapsedSeconds < 0
                || !StatusBallAppearance.TryReadRgb(entry.ColorHex, out _)
                || !JournalCategory.IsValidName(entry.RecordCategory.Name))
            || data.Entries.Select(entry => entry.Id).Distinct().Count() != data.Entries.Count)
        {
            throw new JsonException("记录格式不正确。");
        }

        return data;
    }

    internal JournalData Add(JournalEntry entry) => Update(data => data.Entries.Add(entry with
    {
        Category = FindCategory(data, entry.RecordCategory.Id),
    }));

    internal JournalData Edit(JournalEntry original, string text, JournalCategory selected) => Update(data =>
    {
        var index = data.Entries.FindIndex(entry => entry.Id == original.Id);
        if (index < 0)
        {
            throw new IOException("Record could not be found.");
        }

        var current = data.Entries[index];
        var category = selected == original.RecordCategory ? current.RecordCategory : FindCategory(data, selected.Id);
        data.Entries[index] = current with { Text = text, Category = category };
    });

    internal JournalData AddCategory(string name) => Update(data =>
    {
        ValidateCategoryName(data, name);
        data.Categories.Add(new JournalCategory(Guid.NewGuid(), name));
    });

    internal JournalData RenameCategory(Guid id, string name, bool applyToHistory) => Update(data =>
    {
        var index = EditableCategoryIndex(data, id);
        ValidateCategoryName(data, name, id);
        var renamed = new JournalCategory(id, name);
        data.Categories[index] = renamed;
        if (applyToHistory)
        {
            Reclassify(data, id, renamed);
        }
    });

    internal JournalData DeleteCategory(Guid id, bool applyToHistory) => Update(data =>
    {
        data.Categories.RemoveAt(EditableCategoryIndex(data, id));
        if (applyToHistory)
        {
            Reclassify(data, id, JournalCategory.Ordinary);
        }
    });

    internal JournalData MoveCategory(Guid id, int offset) => Update(data =>
    {
        var index = EditableCategoryIndex(data, id);
        var target = index + offset;
        if (target <= 0 || target >= data.Categories.Count)
        {
            throw new InvalidOperationException("无法移动到该位置。");
        }

        (data.Categories[index], data.Categories[target]) = (data.Categories[target], data.Categories[index]);
    });

    private static JournalCategory FindCategory(JournalData data, Guid id) =>
        data.Categories.Find(category => category.Id == id)
        ?? throw new InvalidOperationException("该类型已不存在，请重新打开输入框选择。");

    private static int EditableCategoryIndex(JournalData data, Guid id)
    {
        var index = data.Categories.FindIndex(category => category.Id == id);
        if (index <= 0)
        {
            throw new InvalidOperationException("该分类不可修改。");
        }

        return index;
    }

    private static void ValidateCategoryName(JournalData data, string name, Guid? except = null)
    {
        if (!JournalCategory.IsValidName(name))
        {
            throw new InvalidOperationException("分类名称不能为空，且不能含空白或斜杠。");
        }
        if (data.Categories.Any(category => category.Id != except
            && category.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("已有同名分类。");
        }
    }

    private static void Reclassify(JournalData data, Guid id, JournalCategory destination)
    {
        for (var index = 0; index < data.Entries.Count; index++)
        {
            var entry = data.Entries[index];
            if (entry.RecordCategory.Id == id)
            {
                data.Entries[index] = entry with { Category = destination };
            }
        }
    }

    private JournalData Update(Action<JournalData> change)
    {
        using var writeLock = AcquireWriteLock();
        var data = Load();
        change(data);
        WriteLocked(data);
        return data;
    }

    internal FileStream AcquireWriteLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal bool IsBackingPath(string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        return new[] { path, path + ".tmp", path + ".lock" }
            .Any(file => fullPath.Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
    }

    internal byte[] ReadUnparsed() => File.ReadAllBytes(path);

    internal void WriteLocked(JournalData data)
    {
        var temporaryPath = path + ".tmp";
        using (var temporary = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(temporary, data);
            temporary.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, null);
        }
        else
        {
            File.Move(temporaryPath, path);
        }

    }
}

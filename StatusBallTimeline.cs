using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using Microsoft.Win32;

namespace BeaconLog;

internal sealed class StatusBallColorRule(int minutes, string colorHex, bool gradient,
    bool rippleReminder = false, int reminderMinutes = StatusBallTimeline.DefaultReminderMinutes, string? title = null)
{
    public int Minutes { get; } = minutes;
    public string ColorHex { get; } = colorHex;
    public bool Gradient { get; } = gradient;
    public bool RippleReminder { get; } = rippleReminder;
    public int ReminderMinutes { get; } = reminderMinutes is >= 1 and <= 99
        ? reminderMinutes : StatusBallTimeline.DefaultReminderMinutes;
    public string Title { get; } = title?.Trim() ?? "";
    [JsonIgnore]
    public string DisplayTitle => Title.Length != 0 ? Title : Minutes == 0 ? "初始颜色" : ColorHex;
}

internal static class StatusBallTimeline
{
    internal const int MinimumRuleMinutes = 10;
    internal const int MaximumRuleMinutes = 99;
    internal const int DefaultReminderMinutes = 10;
    internal const string RulesValue = "StatusBallColorRules";

    internal static List<StatusBallColorRule> Load()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupSettings.SettingsKey);
        if (key?.GetValue(RulesValue) is not string json)
        {
            return [];
        }

        try
        {
            var rules = JsonSerializer.Deserialize<List<StatusBallColorRule?>>(json) ?? [];
            return rules.OfType<StatusBallColorRule>().Where(rule =>
                rule.Minutes is >= MinimumRuleMinutes and <= MaximumRuleMinutes
                && StatusBallAppearance.TryReadRgb(rule.ColorHex, out _)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static void Save(IReadOnlyList<StatusBallColorRule> rules)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupSettings.SettingsKey);
        key.SetValue(RulesValue, JsonSerializer.Serialize(rules), RegistryValueKind.String);
    }

    internal static Color Resolve(string initialHex, IReadOnlyList<StatusBallColorRule> rules, double elapsedMinutes)
    {
        var previous = StatusBallAppearance.ReadColor(initialHex);
        foreach (var rule in rules)
        {
            var target = StatusBallAppearance.ReadColor(rule.ColorHex);
            if (elapsedMinutes < rule.Minutes)
            {
                if (!rule.Gradient)
                {
                    return previous;
                }

                var progress = (double)elapsedMinutes / rule.Minutes;
                byte Mix(byte from, byte to) =>
                    (byte)Math.Round(from + (to - from) * progress, MidpointRounding.AwayFromZero);
                return Color.FromRgb(Mix(previous.R, target.R), Mix(previous.G, target.G), Mix(previous.B, target.B));
            }

            elapsedMinutes -= rule.Minutes;
            previous = target;
        }

        return previous;
    }

    internal static (long Start, long End)? ReminderSegment(IReadOnlyList<StatusBallColorRule> rules, long elapsedMinute)
    {
        long start = 0;
        foreach (var rule in rules)
        {
            var end = start + rule.Minutes;
            if (elapsedMinute <= end)
            {
                var inSegment = elapsedMinute - start;
                return rule.RippleReminder && inSegment > 0 && inSegment % rule.ReminderMinutes == 0
                    ? (start, end) : null;
            }

            start = end;
        }

        return null;
    }
}

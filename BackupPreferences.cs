using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace BeaconLog;

internal sealed record BackupPreferences
{
    public required bool StartAtLogin { get; init; }
    public required bool SilentStartup { get; init; }
    public required bool OpenFloatingWindowAtStartup { get; init; }
    public required bool HideWhenFullscreen { get; init; }
    public required bool AutomaticBackup { get; init; }
    public required int Diameter { get; init; }
    public required string ColorHex { get; init; }
    public required string InitialTitle { get; init; }
    public required List<StatusBallColorRule> ColorRules { get; init; }
    public required string? FloatingPosition { get; init; }

    internal static BackupPreferences Defaults { get; } = new()
    {
        StartAtLogin = false, SilentStartup = false, OpenFloatingWindowAtStartup = false,
        HideWhenFullscreen = true, AutomaticBackup = true,
        Diameter = StatusBallAppearance.DefaultDiameter, ColorHex = StatusBallAppearance.DefaultColor,
        InitialTitle = "", ColorRules = [], FloatingPosition = null,
    };

    internal static BackupPreferences Capture()
    {
        var (diameter, hex, title) = StatusBallAppearance.ReadStoredAppearance();
        using var key = Registry.CurrentUser.OpenSubKey(StartupSettings.SettingsKey);
        var position = key?.GetValue(FloatingStatusWindow.PositionValue) as string;
        return new BackupPreferences
        {
            StartAtLogin = StartupSettings.StartAtLogin, SilentStartup = StartupSettings.SilentStartup,
            OpenFloatingWindowAtStartup = StartupSettings.OpenFloatingWindowAtStartup,
            HideWhenFullscreen = StartupSettings.HideWhenFullscreen, AutomaticBackup = StartupSettings.AutomaticBackup,
            Diameter = diameter, ColorHex = hex, InitialTitle = title, ColorRules = StatusBallTimeline.Load(),
            FloatingPosition = FloatingStatusWindow.ParseSavedPosition(position) is null ? null : position,
        };
    }

    internal void Validate()
    {
        if (Diameter is < StatusBallAppearance.MinimumDiameter or > StatusBallAppearance.MaximumDiameter
            || !StatusBallAppearance.TryReadRgb(ColorHex, out _)
            || InitialTitle is null || StringInfo.ParseCombiningCharacters(InitialTitle).Length > 40
            || ColorRules is null || ColorRules.Any(rule => rule is null
                || rule.Minutes is < StatusBallTimeline.MinimumRuleMinutes or > StatusBallTimeline.MaximumRuleMinutes
                || !StatusBallAppearance.TryReadRgb(rule.ColorHex, out _)
                || StringInfo.ParseCombiningCharacters(rule.Title).Length > 40)
            || (FloatingPosition is not null && FloatingStatusWindow.ParseSavedPosition(FloatingPosition) is null))
        {
            throw new JsonException("备份中的设置格式不正确。");
        }
    }

    internal Action Apply()
    {
        var previousLogin = StartupSettings.GetLoginRegistration();
        var loginChanged = false;
        var previousValues = new List<(string Name, object? Value, RegistryValueKind Kind)>();

        void RollBack()
        {
            using var target = Registry.CurrentUser.OpenSubKey(StartupSettings.SettingsKey, writable: true);
            if (target is not null)
            {
                foreach (var (name, value, kind) in Enumerable.Reverse(previousValues))
                {
                    Write(target, name, value, kind);
                }
            }
            if (loginChanged)
            {
                StartupSettings.SetLoginRegistration(previousLogin);
            }
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupSettings.SettingsKey);
            foreach (var (name, value) in Values())
            {
                var old = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (Equals(old, value))
                {
                    continue;
                }

                var kind = old is null ? RegistryValueKind.Unknown : key.GetValueKind(name);
                Write(key, name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
                previousValues.Add((name, old, kind));
            }

            // A saved Run value must target this installation, never a path from the backup.
            if (StartAtLogin || previousLogin is not null)
            {
                StartupSettings.StartAtLogin = StartAtLogin;
                loginChanged = true;
            }

            return RollBack;
        }
        catch (Exception error) when (BackupService.IsDataError(error))
        {
            try
            {
                RollBack();
            }
            catch (Exception rollbackError) when (BackupService.IsDataError(rollbackError))
            {
                throw new IOException("应用设置失败，部分设置无法回滚。", new AggregateException(error, rollbackError));
            }

            throw;
        }
    }

    private IEnumerable<(string, object?)> Values()
    {
        yield return (StartupSettings.SilentValue, SilentStartup ? 1 : 0);
        yield return (StartupSettings.OpenFloatingWindowValue, OpenFloatingWindowAtStartup ? 1 : 0);
        yield return (StartupSettings.HideWhenFullscreenValue, HideWhenFullscreen ? 1 : 0);
        yield return (StartupSettings.AutomaticBackupValue, AutomaticBackup ? 1 : 0);
        yield return (StatusBallAppearance.DiameterValue, Diameter);
        yield return (StatusBallAppearance.InitialAppearanceValue,
            JsonSerializer.Serialize(new[] { StatusBallAppearance.NormalizeHex(ColorHex), InitialTitle }));
        yield return (StatusBallTimeline.RulesValue, JsonSerializer.Serialize(ColorRules));
        yield return (FloatingStatusWindow.PositionValue, FloatingPosition);
    }

    private static void Write(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, kind);
        }
    }
}

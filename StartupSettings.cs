using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace BeaconLog;

internal static class StartupSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string SettingsKey = @"Software\BeaconLog";
    private const string RunValue = "BeaconLog";
    internal const string SilentValue = "SilentStartup";
    internal const string OpenFloatingWindowValue = "OpenFloatingWindowAtStartup";
    internal const string HideWhenFullscreenValue = "HideWhenFullscreen";
    internal const string AutomaticBackupValue = "AutomaticBackup";
    internal const string LoginArgument = "--startup-at-login";

    public static bool StartAtLogin
    {
        get => GetLoginRegistration() is { Length: > 0 };
        set => SetLoginRegistration(value ? LoginCommand() : null);
    }

    internal static string? GetLoginRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) as string;
    }

    internal static void SetLoginRegistration(string? command)
    {
        if (command is null)
        {
            using var existingKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existingKey?.DeleteValue(RunValue, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(RunValue, command, RegistryValueKind.String);
    }

    internal static void UpgradeLoginRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) is string { Length: > 0 } command
                && !command.EndsWith(" " + LoginArgument, StringComparison.Ordinal))
            {
                key.SetValue(RunValue, LoginCommand(), RegistryValueKind.String);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Keep startup available and retry migration the next time the app starts.
        }
    }

    private static string LoginCommand()
    {
        var processPath = Environment.ProcessPath ?? throw new IOException("无法确定启动程序路径。");
        var command = $"\"{processPath}\"";
        if (string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            command += $" \"{typeof(App).Assembly.Location}\"";
        }

        return $"{command} {LoginArgument}";
    }

    public static bool SilentStartup
    {
        get => ReadFlag(SilentValue);
        set => WriteFlag(SilentValue, value);
    }

    public static bool OpenFloatingWindowAtStartup
    {
        get => ReadFlag(OpenFloatingWindowValue);
        set => WriteFlag(OpenFloatingWindowValue, value);
    }

    public static bool HideWhenFullscreen
    {
        get => ReadFlag(HideWhenFullscreenValue, defaultValue: true);
        set => WriteFlag(HideWhenFullscreenValue, value);
    }

    internal static bool AutomaticBackup
    {
        get => ReadFlag(AutomaticBackupValue, defaultValue: true);
        set => WriteFlag(AutomaticBackupValue, value);
    }

    private static bool ReadFlag(string name, bool defaultValue = false)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return key?.GetValue(name) is int value ? value == 1 : defaultValue;
    }

    private static void WriteFlag(string name, bool value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
    }
}

using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace BeaconLog;

public sealed class StatusBallAppearance : INotifyPropertyChanged
{
    public const int MinimumDiameter = 12;
    public const int MaximumDiameter = 48;
    internal const int DefaultDiameter = 18;
    internal const string DefaultColor = "#08A9C1";
    internal const string DiameterValue = "StatusBallDiameter";
    private const string ColorValue = "StatusBallColor";
    internal const string InitialAppearanceValue = "StatusBallInitialAppearance";
    private const double BackgroundScale = 1.5;

    public static StatusBallAppearance Current { get; } = new();
    private Color displayedColor;

    private StatusBallAppearance()
    {
        (Diameter, ColorHex, InitialTitle) = ReadStoredAppearance();
        displayedColor = ReadColor(ColorHex);
        (Fill, BackgroundFill) = CreateBrushes(displayedColor);
    }

    internal static (int Diameter, string Hex, string Title) ReadStoredAppearance()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupSettings.SettingsKey);
        var diameter = key?.GetValue(DiameterValue) is int size
            && size is >= MinimumDiameter and <= MaximumDiameter ? size : DefaultDiameter;
        var initial = ReadInitialAppearance(key?.GetValue(InitialAppearanceValue) as string);
        var savedColor = initial is { Length: 2 } ? initial[0] : key?.GetValue(ColorValue) as string;
        var title = initial is { Length: 2 } ? initial[1]?.Trim() ?? "" : "";
        return (diameter, TryReadRgb(savedColor, out _) ? NormalizeHex(savedColor!) : DefaultColor, title);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Diameter { get; private set; }
    public string ColorHex { get; private set; }
    internal string InitialTitle { get; private set; }
    public Brush Fill { get; private set; }
    public double BackgroundDiameter => Diameter * BackgroundScale;
    public Brush BackgroundFill { get; private set; }
    internal static double MaximumBackgroundDiameter => MaximumDiameter * BackgroundScale;

    internal void SaveDiameter(int diameter)
    {
        if (Diameter == diameter)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(StartupSettings.SettingsKey);
        key.SetValue(DiameterValue, diameter, RegistryValueKind.DWord);
        Diameter = diameter;
        PropertyChanged?.Invoke(this, new(nameof(Diameter)));
        PropertyChanged?.Invoke(this, new(nameof(BackgroundDiameter)));
    }

    internal void SaveInitialAppearance(string hex, string title)
    {
        var normalized = NormalizeHex(hex);
        if (ColorHex == normalized && InitialTitle == title)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(StartupSettings.SettingsKey);
        key.SetValue(InitialAppearanceValue, JsonSerializer.Serialize(new[] { normalized, title }), RegistryValueKind.String);
        ColorHex = normalized;
        InitialTitle = title;
    }

    internal void ApplyRestoredAppearance(int diameter, string hex, string title)
    {
        Diameter = diameter;
        ColorHex = NormalizeHex(hex);
        InitialTitle = title;
        PropertyChanged?.Invoke(this, new(nameof(Diameter)));
        PropertyChanged?.Invoke(this, new(nameof(BackgroundDiameter)));
    }

    private static string[]? ReadInitialAppearance(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal void ShowTimelineColor(Color color)
    {
        if (displayedColor == color)
        {
            return;
        }

        displayedColor = color;
        (Fill, BackgroundFill) = CreateBrushes(color);
        PropertyChanged?.Invoke(this, new(nameof(Fill)));
        PropertyChanged?.Invoke(this, new(nameof(BackgroundFill)));
    }

    internal static string NormalizeHex(string hex) => "#" + hex.Trim().TrimStart('#').ToUpperInvariant();

    internal static bool TryReadRgb(string? hex, out uint rgb)
    {
        var digits = (hex ?? "").Trim();
        if (digits.StartsWith('#'))
        {
            digits = digits[1..];
        }

        rgb = 0;
        return digits.Length == 6 && uint.TryParse(digits, NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out rgb);
    }

    internal static Color ReadColor(string hex)
    {
        TryReadRgb(hex, out var rgb);
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static (Brush Ball, Brush Background) CreateBrushes(Color color)
    {
        byte Lighten(byte channel) => (byte)Math.Round(channel * 0.35 + 255 * 0.65);
        return (CreateBrush(color), CreateBrush(Color.FromRgb(
            Lighten(color.R), Lighten(color.G), Lighten(color.B))));
    }

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

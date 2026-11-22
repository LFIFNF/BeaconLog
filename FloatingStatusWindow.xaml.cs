using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BeaconLog;

public partial class FloatingStatusWindow : Window
{
    internal const string PositionValue = "FloatingStatusPosition";
    private StatusBallRippleWindow? ripple;
    private Forms.Screen initialScreen;
    private readonly Window mainWindow;
    private Point pointerStart;

    internal FloatingStatusWindow(Window mainWindow)
    {
        InitializeComponent();
        this.mainWindow = mainWindow;
        initialScreen = PreferredScreen(ReadSavedPosition());
        SourceInitialized += (_, _) => ApplyRestoredPosition();

        LocationChanged += (_, _) => ripple?.CenterOnBall();
        SizeChanged += (_, _) => ripple?.CenterOnBall();
    }

    private Forms.Screen PreferredScreen(Drawing.Point? savedPosition)
    {
        if (savedPosition is { } point)
        {
            return Forms.Screen.FromPoint(point);
        }
        var mainHandle = new WindowInteropHelper(mainWindow).Handle;
        return mainWindow.IsVisible ? Forms.Screen.FromHandle(mainHandle)
            : Forms.Screen.PrimaryScreen ?? Forms.Screen.FromHandle(mainHandle);
    }

    internal void ApplyRestoredPosition()
    {
        var savedPosition = ReadSavedPosition();
        initialScreen = PreferredScreen(savedPosition);
        var workArea = initialScreen.WorkingArea;
        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (target is null)
        {
            return;
        }

        if (savedPosition is { } position)
        {
            var diameter = StatusBallAppearance.Current.BackgroundDiameter;
            var pixels = target.TransformToDevice.Transform(new Vector(diameter, diameter));
            var size = new Drawing.Size((int)Math.Ceiling(pixels.X), (int)Math.Ceiling(pixels.Y));
            var visiblePosition = ScreenPlacement.Clamp(position, size, workArea);
            var dip = target.TransformFromDevice.Transform(new Point(visiblePosition.X, visiblePosition.Y));
            Left = dip.X;
            Top = dip.Y;
            return;
        }

        var bottomRight = target.TransformFromDevice.Transform(new Point(workArea.Right, workArea.Bottom));
        // Keep the initial ripple inside the work area even at the largest diameter.
        var inset = StatusBallAppearance.MaximumBackgroundDiameter / 2
            + StatusBallRippleWindow.OverlayDiameter / 2 + 8;
        Left = bottomRight.X - inset;
        Top = bottomRight.Y - inset;
    }

    private static Drawing.Point? ReadSavedPosition()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupSettings.SettingsKey);
        return ParseSavedPosition(key?.GetValue(PositionValue) as string);
    }

    internal static Drawing.Point? ParseSavedPosition(string? value)
    {
        var coordinates = value?.Split(',');
        return coordinates is { Length: 2 }
            && int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            && int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            ? new Drawing.Point(x, y) : null;
    }

    private void SavePosition()
    {
        var position = PointToScreen(new Point(0, 0));
        var value = FormattableString.Invariant($"{(int)Math.Round(position.X)},{(int)Math.Round(position.Y)}");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupSettings.SettingsKey);
            key.SetValue(PositionValue, value, RegistryValueKind.String);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning("Unable to save floating status window position: {0}", error.Message);
        }
    }

    internal Forms.Screen CurrentScreen
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            return handle == 0 ? initialScreen : Forms.Screen.FromHandle(handle);
        }
    }

    internal void HideForFullscreen()
    {
        ripple?.Close();
        Hide();
    }

    internal void PlayLocationCue()
    {
        if (ripple is null)
        {
            ripple = new StatusBallRippleWindow(this, FloatingBall);
            ripple.Closed += (_, _) => ripple = null;
            ripple.Show();
        }

        ripple.Play(SystemParameters.ClientAreaAnimation);
    }

    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        pointerStart = e.GetPosition(this);
        FloatingBall.CaptureMouse();
        e.Handled = true;
    }

    private void Ball_MouseMove(object sender, MouseEventArgs e)
    {
        if (!FloatingBall.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var offset = e.GetPosition(this) - pointerStart;
        if (Math.Abs(offset.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(offset.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            FloatingBall.ReleaseMouseCapture();
            var previous = PointToScreen(new Point(0, 0));
            DragMove();
            if (IsVisible && PointToScreen(new Point(0, 0)) != previous)
            {
                SavePosition();
            }
        }
    }

    private void Ball_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!FloatingBall.IsMouseCaptured)
        {
            return;
        }

        FloatingBall.ReleaseMouseCapture();
        ((App)Application.Current).ShowBallInput(FloatingBall);
        e.Handled = true;
    }

    private void CloseFloatingWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ResetTimer_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).ConfirmTimerReset(this);
}

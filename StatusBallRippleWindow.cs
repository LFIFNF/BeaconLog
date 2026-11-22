using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace BeaconLog;

internal sealed class StatusBallRippleWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const int TransparentStyle = 0x00000020;
    private const int NoActivateStyle = 0x08000000;
    private const double StrokeWidth = 4;
    private const double ExpansionDiameter = 64;
    private static readonly TimeSpan PlaybackTime = TimeSpan.FromMilliseconds(600);
    internal static double OverlayDiameter =>
        StatusBallAppearance.MaximumBackgroundDiameter + ExpansionDiameter + 2 * StrokeWidth + 12;

    private readonly Ellipse ring;
    private readonly DispatcherTimer dismissTimer;
    private readonly FrameworkElement ball;

    internal StatusBallRippleWindow(Window owner, FrameworkElement ball)
    {
        this.ball = ball;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Width = Height = OverlayDiameter;

        ring = new Ellipse
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = StrokeWidth,
            Opacity = 0,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 0, Opacity = 0.4 },
        };
        Content = new Grid { Children = { ring } };

        dismissTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = PlaybackTime };
        dismissTimer.Tick += (_, _) => Close();
        Closed += (_, _) => dismissTimer.Stop();
        CenterOnBall();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        // With a layered window, this style passes input through even on painted pixels.
        var style = GetWindowLong(handle, ExtendedStyleIndex);
        SetWindowLong(handle, ExtendedStyleIndex, style | TransparentStyle | NoActivateStyle);
    }

    internal void CenterOnBall()
    {
        var target = PresentationSource.FromVisual(ball)?.CompositionTarget;
        if (target is null)
        {
            return;
        }

        var center = ball.PointToScreen(new Point(ball.ActualWidth / 2, ball.ActualHeight / 2));
        center = target.TransformFromDevice.Transform(center);
        Left = center.X - Width / 2;
        Top = center.Y - Height / 2;
    }

    internal void Play(bool animate)
    {
        dismissTimer.Stop();
        CenterOnBall();
        var appearance = StatusBallAppearance.Current;
        var startDiameter = appearance.BackgroundDiameter + StrokeWidth;
        ring.Stroke = appearance.Fill;
        ring.BeginAnimation(WidthProperty, null);
        ring.BeginAnimation(HeightProperty, null);
        ring.BeginAnimation(OpacityProperty, null);
        ring.Width = ring.Height = startDiameter;
        ring.Opacity = 0.95;

        if (animate)
        {
            var expand = new DoubleAnimation(startDiameter, startDiameter + ExpansionDiameter, PlaybackTime)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            ring.BeginAnimation(WidthProperty, expand);
            ring.BeginAnimation(HeightProperty, expand);
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.95, 0, PlaybackTime));
        }

        dismissTimer.Start();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BeaconLog;

internal partial class TimerResetConfirmation : Window
{
    private readonly FrameworkElement anchor;
    private readonly Window anchorWindow;
    private readonly bool preferBeside;
    private readonly Action confirm;
    private bool closing;

    internal TimerResetConfirmation(FrameworkElement anchor, bool preferBeside, Action confirm)
    {
        this.anchor = anchor;
        this.preferBeside = preferBeside;
        this.confirm = confirm;
        InitializeComponent();
        anchorWindow = GetWindow(anchor);
        Owner = anchorWindow;
        Topmost = anchorWindow.Topmost;
        Loaded += (_, _) =>
        {
            FocusManager.SetFocusedElement(this, CancelButton);
            CancelButton.Focus();
        };
        Deactivated += (_, _) => Dismiss();
        anchor.IsVisibleChanged += AnchorVisibilityChanged;
        anchorWindow.StateChanged += OwnerStateChanged;
        anchorWindow.LocationChanged += OwnerLocationChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        PositionNearAnchor();
    }

    private void PositionNearAnchor()
    {
        var source = PresentationSource.FromVisual(anchor)?.CompositionTarget;
        var target = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (source is null || target is null || !anchor.IsVisible)
        {
            return;
        }

        var start = anchor.PointToScreen(new Point());
        var end = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var anchorRect = Drawing.Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y),
            (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
        var center = new Drawing.Point(anchorRect.Left + anchorRect.Width / 2, anchorRect.Top + anchorRect.Height / 2);
        var screen = Forms.Screen.FromPoint(center);
        var pixels = source.TransformToDevice.Transform(new Vector(Width, Height));
        var size = new Drawing.Size((int)Math.Ceiling(pixels.X), (int)Math.Ceiling(pixels.Y));
        var gap = (int)Math.Ceiling(6 * source.TransformToDevice.M11);
        var position = ScreenPlacement.Near(anchorRect, size, screen.WorkingArea, gap, preferBeside);
        var dip = target.TransformFromDevice.Transform(new Point(position.X, position.Y));
        Left = dip.X;
        Top = dip.Y;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            e.Handled = true;
            if (e.Key == Key.Enter && ConfirmButton.IsKeyboardFocusWithin)
            {
                Confirm();
            }
            else
            {
                Dismiss();
            }
        }
        base.OnPreviewKeyDown(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void Confirm_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Confirm()
    {
        if (!closing)
        {
            Close();
            confirm();
        }
    }

    private void Dismiss()
    {
        if (!closing)
        {
            Close();
        }
    }

    private void AnchorVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!anchor.IsVisible)
        {
            Dismiss();
        }
    }

    private void OwnerStateChanged(object? sender, EventArgs e)
    {
        if (anchorWindow.WindowState == WindowState.Minimized)
        {
            Dismiss();
        }
    }

    private void OwnerLocationChanged(object? sender, EventArgs e) => PositionNearAnchor();

    protected override void OnClosing(CancelEventArgs e)
    {
        closing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = true;
        anchor.IsVisibleChanged -= AnchorVisibilityChanged;
        anchorWindow.StateChanged -= OwnerStateChanged;
        anchorWindow.LocationChanged -= OwnerLocationChanged;
        base.OnClosed(e);
    }
}

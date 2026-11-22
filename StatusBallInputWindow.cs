using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BeaconLog;

internal sealed class StatusBallInputWindow : Window
{
    private readonly FrameworkElement ball;
    private readonly Window ballWindow;
    private readonly TextBox input;
    private readonly ComboBox categorySelector;
    private readonly IReadOnlyList<JournalCategory> categories;
    private readonly TextBlock errorText;
    private readonly Func<string, JournalCategory, Task<string?>> save;
    private bool closing;

    internal StatusBallInputWindow(FrameworkElement ball, string text, IReadOnlyList<JournalCategory> categories,
        JournalCategory selected, Func<string, JournalCategory, Task<string?>> save, bool isEdit)
    {
        this.ball = ball;
        this.categories = categories;
        this.save = save;
        ballWindow = GetWindow(ball);
        Owner = ballWindow;
        Topmost = ballWindow.Topmost;
        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Title = isEdit ? "修改记录" : "添加记录";
        Width = 394;
        Height = 64;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        var handle = new Border
        {
            Height = 13,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Child = new Border
            {
                Width = 26,
                Height = 3,
                Background = new SolidColorBrush(Color.FromRgb(193, 205, 214)),
                CornerRadius = new CornerRadius(2),
            },
        };
        handle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            DragMove();
        };

        input = new TextBox
        {
            Text = text,
            Height = 34,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(167, 190, 201)),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AutomationProperties.SetName(input, "记录正文");
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && TryApplyCategoryCommand())
            {
                e.Handled = true;
            }
        };
        categorySelector = new ComboBox
        {
            Height = 34,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            DisplayMemberPath = nameof(JournalCategory.Name),
            ItemsSource = categories.Contains(selected) ? categories : categories.Append(selected).ToArray(),
            SelectedItem = selected,
            MaxDropDownHeight = 200,
            Style = (Style)FindResource("CompactCategorySelector"),
        };
        AutomationProperties.SetName(categorySelector, "类型");
        categorySelector.SelectionChanged += (_, _) => UpdateCategorySelector();
        UpdateCategorySelector();
        var submit = new Button
        {
            Content = isEdit ? "保存" : "提交",
            Height = 34,
            Padding = new Thickness(13, 4, 13, 4),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(222, 243, 245)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(156, 204, 209)),
        };
        submit.Click += (_, _) => Submit();
        DockPanel.SetDock(categorySelector, Dock.Left);
        DockPanel.SetDock(submit, Dock.Right);
        var body = new DockPanel { Margin = new Thickness(9, 4, 9, 0), Children = { categorySelector, submit, input } };
        errorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(166, 39, 39)),
            Margin = new Thickness(9, 4, 9, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        Content = new Border
        {
            Child = new StackPanel { Children = { handle, body, errorText } },
            Background = Brushes.White,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };

        ball.IsVisibleChanged += BallVisibilityChanged;
        ballWindow.StateChanged += OwnerStateChanged;
        Loaded += (_, _) =>
        {
            UpdateCategorySelector();
            input.Focus();
            input.CaretIndex = input.Text.Length;
        };
        Deactivated += (_, _) => CloseInput();
    }

    internal bool WasSaved { get; private set; }
    internal bool IsSaving { get; private set; }
    internal bool WasDismissedByKeyboard { get; private set; }
    internal string UnsubmittedText => input.Text;
    internal JournalCategory SelectedCategory => (JournalCategory?)categorySelector.SelectedItem ?? JournalCategory.Ordinary;

    private void UpdateCategorySelector()
    {
        var name = SelectedCategory.Name;
        categorySelector.ToolTip = $"类型：{name}";
        var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), categorySelector.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        categorySelector.Width = Math.Clamp(Math.Ceiling(text.Width) + 24, 52, 112);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (transform is null)
        {
            return;
        }

        var start = ball.PointToScreen(new Point(0, 0));
        var end = ball.PointToScreen(new Point(ball.ActualWidth, ball.ActualHeight));
        var screen = Forms.Screen.FromPoint(new Drawing.Point((int)start.X, (int)start.Y));
        var area = screen.WorkingArea;
        var pixels = transform.TransformToDevice.Transform(new Vector(Width, Height));
        var size = new Drawing.Size((int)Math.Ceiling(pixels.X), (int)Math.Ceiling(pixels.Y));
        var gap = (int)Math.Ceiling(6 * transform.TransformToDevice.M11);
        var x = (int)Math.Ceiling(end.X) + gap;
        if ((long)x + size.Width > area.Right)
        {
            x = (int)Math.Floor(start.X) - gap - size.Width;
        }

        var position = ScreenPlacement.Clamp(new Drawing.Point(x, (int)start.Y), size, area);
        var dip = transform.TransformFromDevice.Transform(new Point(position.X, position.Y));
        Left = dip.X;
        Top = dip.Y;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (categorySelector.IsDropDownOpen)
        {
            return;
        }

        if (e.Key == Key.Enter && !categorySelector.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Submit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            WasDismissedByKeyboard = true;
            CloseInput();
        }
    }

    private bool TryApplyCategoryCommand()
    {
        if (input.SelectionLength != 0 || input.CaretIndex != input.Text.Length
            || JournalCategory.Match(categories, input.Text) is not { } match)
        {
            return false;
        }

        categorySelector.SelectedItem = match;
        input.Clear();
        return true;
    }

    private async void Submit()
    {
        if (IsSaving)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(input.Text))
        {
            ShowError("请输入要保存的内容。");
            return;
        }

        IsSaving = true;
        var content = (UIElement)Content;
        content.IsEnabled = false;
        string? error;
        try
        {
            error = await save(input.Text.Trim(), SelectedCategory);
        }
        finally
        {
            IsSaving = false;
            content.IsEnabled = true;
        }
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        WasSaved = true;
        input.Clear();
        CloseInput();
    }

    private void ShowError(string error)
    {
        errorText.Text = error;
        errorText.Visibility = Visibility.Visible;
        Height = 88;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } transform)
        {
            var origin = PointToScreen(new Point());
            var location = new Drawing.Point((int)origin.X, (int)origin.Y);
            var area = Forms.Screen.FromPoint(location).WorkingArea;
            var pixels = transform.TransformToDevice.Transform(new Vector(Width, Height));
            var position = ScreenPlacement.Clamp(location,
                new Drawing.Size((int)Math.Ceiling(pixels.X), (int)Math.Ceiling(pixels.Y)), area);
            var dip = transform.TransformFromDevice.Transform(new Point(position.X, position.Y));
            Left = dip.X;
            Top = dip.Y;
        }
        input.Focus();
    }

    private void BallVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!ball.IsVisible)
        {
            CloseInput();
        }
    }

    private void OwnerStateChanged(object? sender, EventArgs e)
    {
        if (ballWindow.WindowState == WindowState.Minimized)
        {
            CloseInput();
        }
    }

    private void CloseInput()
    {
        if (!closing && !IsSaving)
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsSaving)
        {
            e.Cancel = true;
            return;
        }
        closing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        closing = true;
        ball.IsVisibleChanged -= BallVisibilityChanged;
        ballWindow.StateChanged -= OwnerStateChanged;
        base.OnClosed(e);
    }
}

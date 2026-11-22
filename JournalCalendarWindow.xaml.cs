using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BeaconLog;

public partial class JournalCalendarWindow : Window
{
    private static readonly Brush[] HeatColors =
    [
        Brushes.Transparent, new SolidColorBrush(Color.FromRgb(226, 244, 243)),
        new SolidColorBrush(Color.FromRgb(183, 226, 224)), new SolidColorBrush(Color.FromRgb(105, 185, 181)),
        new SolidColorBrush(Color.FromRgb(19, 111, 115)),
    ];
    private static readonly Brush DayText = new SolidColorBrush(Color.FromRgb(36, 52, 68));
    private static readonly Brush DateOutline = new SolidColorBrush(Color.FromRgb(23, 99, 105));
    private readonly FrameworkElement anchor;
    private readonly Dictionary<DateOnly, Button> dayButtons = [];
    private readonly DispatcherTimer hoverTimer;
    private readonly DispatcherTimer dismissTimer;
    private ILookup<DateOnly, JournalEntry> records;
    private DateOnly today;
    private DateOnly month;
    private DateOnly? hoveredDay;
    private DateOnly? previewDay;
    private bool pinned;
    private bool editing;
    private bool closing;
    private bool updatingCategoryFilter;

    internal JournalCalendarWindow(FrameworkElement anchor, ILookup<DateOnly, JournalEntry> records, DateOnly today)
    {
        this.anchor = anchor;
        this.records = records;
        this.today = today;
        InitializeComponent();
        Owner = GetWindow(anchor);

        hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (!pinned && hoveredDay is { } date && dayButtons[date].IsMouseOver)
            {
                ShowPreview(date);
            }
        };
        dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        dismissTimer.Tick += (_, _) =>
        {
            dismissTimer.Stop();
            if (!pinned && hoveredDay is null && !PreviewPanel.IsMouseOver)
            {
                ClearPreview();
            }
        };
        Deactivated += (_, _) =>
        {
            if (!editing && !closing)
            {
                Close();
            }
        };
        ShowMonth(today);
        UpdateCategoryFilter();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (transform is null)
        {
            return;
        }

        var start = anchor.PointToScreen(new Point());
        var below = anchor.PointToScreen(new Point(0, anchor.ActualHeight));
        var area = Forms.Screen.FromPoint(new Drawing.Point((int)start.X, (int)start.Y)).WorkingArea;
        Width = Math.Min(Width, area.Width / transform.TransformToDevice.M11);
        Height = Math.Min(Height, area.Height / transform.TransformToDevice.M22);
        var pixels = transform.TransformToDevice.Transform(new Vector(Width, Height));
        var size = new Drawing.Size((int)Math.Ceiling(pixels.X), (int)Math.Ceiling(pixels.Y));
        var y = (int)Math.Ceiling(below.Y) + 6;
        if ((long)y + size.Height > area.Bottom)
        {
            y = (int)Math.Floor(start.Y) - size.Height - 6;
        }

        var location = ScreenPlacement.Clamp(new Drawing.Point((int)start.X, y), size, area);
        var dip = transform.TransformFromDevice.Transform(new Point(location.X, location.Y));
        Left = dip.X;
        Top = dip.Y;
    }

    internal void UpdateContext(ILookup<DateOnly, JournalEntry> entries, DateOnly currentDay, JournalEntry? edited = null)
    {
        records = entries;
        today = currentDay;
        RefreshDayStyles();
        if (previewDay is { } date)
        {
            PopulatePreview(date, edited);
        }
        UpdateCategoryFilter(edited);
    }

    private void UpdateCategoryFilter(JournalEntry? edited = null)
    {
        var selected = CategoryFilter.SelectedItem as string;
        var active = ((App)Application.Current).JournalCategories.Select(category => category.Name);
        var names = active.Concat(records.SelectMany(day => day)
            .Select(entry => entry.RecordCategory.Name)
            .OrderBy(name => name, StringComparer.CurrentCulture))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        updatingCategoryFilter = true;
        CategoryFilter.ItemsSource = names;
        CategoryFilter.SelectedItem = names.FirstOrDefault(name =>
            name.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? names[0];
        updatingCategoryFilter = false;
        ShowCategoryHistory(edited);
    }

    private void CategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingCategoryFilter)
        {
            ShowCategoryHistory();
        }
    }

    private void CategoryHistoryList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (CategoryHistoryList.IsVisible)
        {
            JournalListView.ScrollToEnd(CategoryHistoryList);
        }
    }

    private void ShowCategoryHistory(JournalEntry? edited = null)
    {
        var selected = CategoryFilter.SelectedItem as string;
        var entries = records.OrderBy(day => day.Key).SelectMany(day => day)
            .Where(entry => entry.RecordCategory.Name.Equals(selected, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CategoryCount.Text = $"{entries.Length} 条记录";
        CategoryEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (CategoryHistoryList.ItemsSource is IEnumerable<JournalEntry> displayed && displayed.SequenceEqual(entries))
        {
            return;
        }
        CategoryHistoryList.ItemsSource = entries;
        if (edited is not null && entries.Contains(edited))
        {
            CategoryHistoryList.SelectedItem = edited;
            CategoryHistoryList.ScrollIntoView(edited);
        }
        else
        {
            JournalListView.ScrollToEnd(CategoryHistoryList);
        }
    }

    private void ShowMonth(DateOnly date)
    {
        ClearPreview();
        hoverTimer.Stop();
        hoveredDay = null;
        month = new DateOnly(date.Year, date.Month, 1);
        MonthTitle.Text = month.ToString("yyyy年M月", CultureInfo.InvariantCulture);
        PreviousMonth.IsEnabled = month != DateOnly.MinValue;
        NextMonth.IsEnabled = month.Year < 9999 || month.Month < 12;
        DaysGrid.Children.Clear();
        dayButtons.Clear();
        var offset = ((int)month.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        for (var index = 0; index < 42; index++)
        {
            var dayNumber = index - offset + 1;
            if (dayNumber < 1 || dayNumber > days)
            {
                DaysGrid.Children.Add(new Border());
                continue;
            }

            var day = new DateOnly(month.Year, month.Month, dayNumber);
            var button = new Button
            {
                Content = dayNumber.ToString(CultureInfo.InvariantCulture),
                Tag = day,
                Style = (Style)FindResource("CalendarDay"),
            };
            button.MouseEnter += Day_MouseEnter;
            button.MouseLeave += Day_MouseLeave;
            button.GotKeyboardFocus += Day_GotKeyboardFocus;
            button.PreviewKeyDown += Day_PreviewKeyDown;
            button.Click += (_, _) =>
            {
                pinned = true;
                ShowPreview(day);
            };
            dayButtons.Add(day, button);
            DaysGrid.Children.Add(button);
        }

        RefreshDayStyles();
    }

    private void RefreshDayStyles()
    {
        var counts = dayButtons.Keys.ToDictionary(date => date, date => records[date].Count());
        var maximum = counts.Values.Max();
        foreach (var (day, button) in dayButtons)
        {
            var count = counts[day];
            var tier = HeatTier(count, maximum);
            button.Background = HeatColors[tier];
            button.Foreground = tier == 4 ? Brushes.White : DayText;
            button.ToolTip = $"{count} 条记录";
            AutomationProperties.SetName(button, $"{day:yyyy年M月d日}，{count} 条记录");
        }

        UpdateDateOutlines();
    }

    internal static int HeatTier(int count, int maximum) => count == 0 || maximum == 0
        ? 0 : (int)((4L * count + maximum - 1) / maximum);

    private void UpdateDateOutlines()
    {
        foreach (var (day, button) in dayButtons)
        {
            button.BorderBrush = day == previewDay || day == today ? DateOutline : Brushes.Transparent;
        }
    }

    private void ShowPreview(DateOnly date)
    {
        dismissTimer.Stop();
        var changingDay = previewDay != date;
        previewDay = date;
        PreviewPanel.Visibility = Visibility.Visible;
        PinnedStatus.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        PopulatePreview(date, scrollToEnd: changingDay);
        UpdateDateOutlines();
    }

    private void PopulatePreview(DateOnly date, JournalEntry? edited = null, bool scrollToEnd = false)
    {
        var entries = records[date].ToArray();
        PreviewDate.Text = date.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
        PreviewCount.Text = $"{entries.Length} 条记录";
        PreviewEmpty.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewList.ItemsSource is not IEnumerable<JournalEntry> displayed || !displayed.SequenceEqual(entries))
        {
            PreviewList.ItemsSource = entries;
            scrollToEnd = true;
        }

        if (edited?.RecordedDate == date)
        {
            PreviewList.SelectedItem = edited;
            PreviewList.ScrollIntoView(edited);
        }
        else if (scrollToEnd)
        {
            JournalListView.ScrollToEnd(PreviewList);
        }
    }

    private void ClearPreview()
    {
        dismissTimer.Stop();
        pinned = false;
        previewDay = null;
        PreviewPanel.Visibility = Visibility.Hidden;
        PreviewList.ItemsSource = null;
        UpdateDateOutlines();
    }

    private void Day_MouseEnter(object sender, MouseEventArgs e)
    {
        hoveredDay = (DateOnly)((Button)sender).Tag;
        hoverTimer.Stop();
        dismissTimer.Stop();
        if (!pinned)
        {
            hoverTimer.Start();
        }
    }

    private void Day_MouseLeave(object sender, MouseEventArgs e)
    {
        if (hoveredDay == (DateOnly)((Button)sender).Tag)
        {
            hoveredDay = null;
            hoverTimer.Stop();
            SchedulePreviewDismissal();
        }
    }

    private void SchedulePreviewDismissal()
    {
        if (!pinned)
        {
            dismissTimer.Stop();
            dismissTimer.Start();
        }
    }

    private void PreviewPanel_MouseEnter(object sender, MouseEventArgs e) => dismissTimer.Stop();
    private void PreviewPanel_MouseLeave(object sender, MouseEventArgs e) => SchedulePreviewDismissal();

    private void Day_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!pinned)
        {
            hoverTimer.Stop();
            ShowPreview((DateOnly)((Button)sender).Tag);
        }
    }

    private void Day_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var day = (DateOnly)((Button)sender).Tag;
        if (e.Key == Key.Enter)
        {
            pinned = true;
            ShowPreview(day);
            e.Handled = true;
            return;
        }

        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (offset == 0)
        {
            return;
        }

        var targetNumber = day.DayNumber + offset;
        if (targetNumber >= DateOnly.MinValue.DayNumber && targetNumber <= DateOnly.MaxValue.DayNumber)
        {
            var target = DateOnly.FromDayNumber(targetNumber);
            if (!dayButtons.ContainsKey(target))
            {
                ShowMonth(target);
            }

            dayButtons[target].Focus();
        }

        e.Handled = true;
    }

    private void Calendar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        hoverTimer.Stop();
        if (pinned && HistoryTabs.SelectedItem == DateTab)
        {
            TodayButton.Focus();
            ClearPreview();
        }
        else
        {
            Close();
        }
        e.Handled = true;
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e) => ShowMonth(month.AddMonths(-1));
    private void NextMonth_Click(object sender, RoutedEventArgs e) => ShowMonth(month.AddMonths(1));

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        ShowMonth(today);
        dayButtons[today].Focus();
    }

    private void PreviewRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { Content: JournalEntry entry } row)
        {
            EditEntry(row, entry, ItemsControl.ItemsControlFromItemContainer(row) == PreviewList);
            e.Handled = true;
        }
    }

    private void PreviewList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var list = (ListBox)sender;
        if (list.SelectedItem is not JournalEntry entry)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            var row = list.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement;
            EditEntry(row ?? list, entry, list == PreviewList);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            try
            {
                Clipboard.SetText(entry.Text);
            }
            catch (ExternalException)
            {
                // Another process is holding the clipboard.
            }
            e.Handled = true;
        }
    }

    private void EditEntry(FrameworkElement row, JournalEntry entry, bool datePreview)
    {
        if (datePreview)
        {
            pinned = true;
            ShowPreview(entry.RecordedDate);
        }
        editing = true;
        var editor = ((App)Application.Current).ShowJournalEditor(row, entry);
        editing = editor.IsVisible;
        editor.Closed += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                editing = false;
                if (closing || !IsVisible)
                {
                    return;
                }

                if (editor.WasSaved || editor.WasDismissedByKeyboard)
                {
                    Activate();
                }
                else if (!IsActive)
                {
                    Close();
                }
            });
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        closing = true;
        hoverTimer.Stop();
        dismissTimer.Stop();
        base.OnClosing(e);
    }
}

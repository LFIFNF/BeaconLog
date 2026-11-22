using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BeaconLog;

public partial class MainWindow : Window
{
    private StatusBallRippleWindow? ripple;
    private bool scrollJournalWhenVisible;
    private DateOnly today = DateOnly.FromDateTime(DateTime.Now);
    private ILookup<DateOnly, JournalEntry> journalByDate = Array.Empty<JournalEntry>().ToLookup(entry => entry.RecordedDate);
    private JournalCalendarWindow? journalCalendar;
    private readonly TimerResetPress resetPress = new();
    private readonly DispatcherTimer resetHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private long resetPressStarted;

    public MainWindow()
    {
        InitializeComponent();
        resetHoldTimer.Tick += (_, _) =>
        {
            if (Mouse.LeftButton != MouseButtonState.Pressed || !ResetTimerButton.IsMouseOver)
            {
                CancelResetPress();
                return;
            }

            AdvanceResetPress();
        };
        LocationChanged += (_, _) => ripple?.CenterOnBall();
        SizeChanged += (_, _) => ripple?.CenterOnBall();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                ripple?.Close();
                journalCalendar?.Close();
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CancelResetPress();
                ripple?.Close();
            }
            else if (scrollJournalWhenVisible)
            {
                ScrollJournalToLatest();
            }
        };
    }

    private void ResetTimer_Press(object sender, MouseButtonEventArgs e)
    {
        resetPress.Begin();
        resetPressStarted = Stopwatch.GetTimestamp();
        ResetTimerProgress.Value = 0;
        ResetTimerProgress.Visibility = Visibility.Visible;
        resetHoldTimer.Start();
    }

    private void ResetTimer_Release(object sender, MouseButtonEventArgs e)
    {
        if (ResetTimerButton.IsMouseOver)
        {
            AdvanceResetPress();
        }
        else
        {
            CancelResetPress();
        }

        if (resetPress.End())
        {
            e.Handled = true;
            ResetTimerButton.ReleaseMouseCapture();
        }
        resetHoldTimer.Stop();
        ResetTimerProgress.Visibility = Visibility.Hidden;
    }

    private void ResetTimer_Cancel(object sender, MouseEventArgs e) => CancelResetPress();

    private void CancelResetPress()
    {
        resetPress.Cancel();
        resetHoldTimer.Stop();
        ResetTimerProgress.Visibility = Visibility.Hidden;
    }

    private void AdvanceResetPress()
    {
        if (!resetPress.Active)
        {
            return;
        }
        if (resetPress.Advance(Stopwatch.GetElapsedTime(resetPressStarted)))
        {
            resetHoldTimer.Stop();
            ((App)Application.Current).ResetTiming(DateTimeOffset.UtcNow);
        }
        ResetTimerProgress.Value = resetPress.Progress;
    }

    private void ResetTimer_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).ShowTimerResetConfirmation(ResetTimerButton);

    internal void ShowJournal(IReadOnlyList<JournalEntry> entries, JournalEntry? edited = null)
    {
        JournalError.Visibility = Visibility.Collapsed;
        journalByDate = entries.ToLookup(entry => entry.RecordedDate);
        ShowToday(edited);
        journalCalendar?.UpdateContext(journalByDate, today, edited);
    }

    internal void UpdateJournalDate(DateTimeOffset now)
    {
        var currentDate = DateOnly.FromDateTime(now.DateTime);
        if (today != currentDate)
        {
            today = currentDate;
            ShowToday();
            journalCalendar?.UpdateContext(journalByDate, today);
        }
    }

    private void ShowToday(JournalEntry? edited = null)
    {
        var entries = journalByDate[today].ToArray();
        JournalEmpty.Visibility = entries.Length == 0 && JournalError.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        if (JournalList.ItemsSource is IEnumerable<JournalEntry> displayed && displayed.SequenceEqual(entries))
        {
            return;
        }

        JournalList.ItemsSource = entries;
        if (edited is not null && edited.RecordedDate == today)
        {
            JournalList.SelectedItem = edited;
            JournalList.ScrollIntoView(edited);
        }
        else
        {
            ScrollJournalToLatest();
        }
    }

    internal void ShowJournalLoadError()
    {
        JournalEmpty.Visibility = Visibility.Collapsed;
        JournalError.Text = "暂时无法读取本地记录。";
        JournalError.Visibility = Visibility.Visible;
    }

    private void ScrollJournalToLatest()
    {
        scrollJournalWhenVisible = !IsVisible;
        if (!scrollJournalWhenVisible)
        {
            JournalListView.ScrollToEnd(JournalList);
        }
    }

    private void JournalDate_Click(object sender, RoutedEventArgs e)
    {
        if (journalCalendar is not null)
        {
            journalCalendar.Activate();
            return;
        }

        journalCalendar = new JournalCalendarWindow(JournalDateButton, journalByDate, today);
        journalCalendar.Closed += (_, _) => journalCalendar = null;
        journalCalendar.Show();
    }

    private void JournalRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && sender is ListBoxItem { Content: JournalEntry entry } row)
        {
            ((App)Application.Current).ShowJournalEditor(row, entry);
            e.Handled = true;
        }
    }

    private static ListBoxItem? JournalMenuRow(object sender) =>
        sender is MenuItem menu && ItemsControl.ItemsControlFromItemContainer(menu) is ContextMenu context
            ? context.PlacementTarget as ListBoxItem : null;

    private void JournalMenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (JournalMenuRow(sender) is { Content: JournalEntry entry } row)
        {
            ((App)Application.Current).ShowJournalEditor(row, entry);
        }
    }

    private async void JournalMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (JournalMenuRow(sender) is not { Content: JournalEntry entry })
        {
            return;
        }

        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(entry.Text);
        var summary = elements.Length > 70 ? entry.Text[..elements[70]] + "…" : entry.Text;
        if (MessageBox.Show(this, $"删除这条记录？\n\n{summary}", "删除记录",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var error = await ((App)Application.Current).DeleteJournalEntryAsync(entry.Id);
        if (error is not null)
        {
            MessageBox.Show(this, error, "删除记录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void JournalList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && JournalList.SelectedItem is JournalEntry entry)
        {
            var row = JournalList.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement;
            ((App)Application.Current).ShowJournalEditor(row ?? JournalList, entry);
            e.Handled = true;
        }
    }

    private void Ball_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ShowBallInput(MainBall);
    }

    internal void UpdateElapsedTime(string text) => ElapsedStatus.Text = text;

    internal void PlayLocationCue()
    {
        if (ripple is null)
        {
            ripple = new StatusBallRippleWindow(this, MainBall);
            ripple.Closed += (_, _) => ripple = null;
            ripple.Show();
        }

        ripple.Play(SystemParameters.ClientAreaAnimation);
    }

    internal void CloseAuxiliaryWindows()
    {
        foreach (var window in OwnedWindows.OfType<Window>().ToArray())
        {
            window.Close();
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowAuxiliaryWindow<SettingsWindow>();
    }

    private void ToggleFloatingWindow_Click(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ToggleFloatingWindow();
    }

    internal void UpdateFloatingStatus(bool opened, bool visible)
    {
        FloatingToggle.Content = FindResource(opened ? "CloseFloatingIcon" : "OpenFloatingIcon");
        FloatingToggle.ToolTip = opened ? "关闭悬浮窗" : "打开悬浮窗";
        var status = !opened ? "未开启悬浮窗" : visible ? "正在悬浮中" : "全屏隐藏中";
        if (FloatingStatus.Text != status)
        {
            FloatingStatus.Text = status;
        }
    }

    private void OpenJournalCategories_Click(object sender, RoutedEventArgs e)
    {
        ShowAuxiliaryWindow<JournalCategoriesWindow>();
    }

    private void OpenStatusBallSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowAuxiliaryWindow<StatusBallSettingsWindow>();
    }

    private void ShowAuxiliaryWindow<TWindow>() where TWindow : Window, new()
    {
        var window = OwnedWindows.OfType<TWindow>().FirstOrDefault();
        if (window is null)
        {
            new TWindow { Owner = this }.Show();
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }
}

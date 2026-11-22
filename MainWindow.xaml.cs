using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BeaconLog;

public partial class MainWindow : Window
{
    private StatusBallRippleWindow? ripple;
    private long displayedMinute;
    private bool scrollJournalWhenVisible;
    private DateOnly today = DateOnly.FromDateTime(DateTime.Now);
    private ILookup<DateOnly, JournalEntry> journalByDate = Array.Empty<JournalEntry>().ToLookup(entry => entry.RecordedDate);
    private JournalCalendarWindow? journalCalendar;

    public MainWindow()
    {
        InitializeComponent();
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
                ripple?.Close();
            }
            else if (scrollJournalWhenVisible)
            {
                ScrollJournalToLatest();
            }
        };
    }

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

    internal void UpdateElapsedTime(long minute)
    {
        if (displayedMinute != minute)
        {
            displayedMinute = minute;
            ElapsedStatus.Text = $"已经过 {minute} 分钟";
        }
    }

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

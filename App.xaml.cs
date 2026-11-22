using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace BeaconLog;

public partial class App : Application
{
    private Forms.NotifyIcon? trayIcon;
    private Icon? trayArtwork;
    private Forms.ContextMenuStrip? trayMenu;
    private bool exiting;
    private Mutex? instanceMutex;
    private EventWaitHandle? restoreRequest;
    private RegisteredWaitHandle? restoreListener;
    private bool ownsInstance;
    private FloatingStatusWindow? floatingWindow;
    private DispatcherTimer? fullscreenTimer;
    private bool hideWhenFullscreen;
    private DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private long? displayedMinute;
    private IReadOnlyList<StatusBallColorRule> colorRules = [];
    private DispatcherTimer? timelineTimer;
    private readonly SleepIntervalDetector sleepDetector = new();
    private TimerResetConfirmation? timerResetConfirmation;
    private bool watchingPower;
    private DateTimeOffset lastTimelineTick;
    private long lastReminderMinute;
    private (long Start, long End)? pendingReminder;
    private StatusBallInputWindow? inputWindow;
    private readonly JournalStore journal = JournalStore.ForCurrentUser();
    private BackupService? backups;
    private DispatcherTimer? backupTimer;
    private readonly SemaphoreSlim storageGate = new(1);
    private bool checkingBackup;
    internal BackupService Backups => backups ??= new BackupService(journal, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeaconLog", "Backups"));
    internal IReadOnlyList<JournalCategory> JournalCategories { get; private set; } = [JournalCategory.Ordinary];
    private DispatcherTimer? draftTimer;
    private (string Text, JournalCategory Category)? journalDraft;
    private long draftSavedAt;
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(1);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var launchedAtLogin = e.Args.Contains(StartupSettings.LoginArgument, StringComparer.Ordinal);
        using var identity = WindowsIdentity.GetCurrent();
        var instanceName = $@"Local\BeaconLog.{identity.User!.Value}";
        restoreRequest = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Restore");
        instanceMutex = new Mutex(false, instanceName);
        try
        {
            ownsInstance = instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsInstance = true;
        }

        if (!ownsInstance)
        {
            if (!launchedAtLogin)
            {
                restoreRequest.Set();
            }

            Shutdown();
            return;
        }

        try
        {
            if (Backups.RecoverInterruptedRestore() is { } settings)
            {
                StatusBallAppearance.Current.ApplyRestoredAppearance(settings.Diameter, settings.ColorHex, settings.InitialTitle);
            }
        }
        catch (Exception error) when (BackupService.IsDataError(error))
        {
            MessageBox.Show($"未能回滚上次中断的恢复。\n{error.Message}", "BeaconLog",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        StartupSettings.UpgradeLoginRegistration();
        hideWhenFullscreen = StartupSettings.HideWhenFullscreen;
        var window = new MainWindow();
        MainWindow = window;
        window.Closing += HideMainWindow;
        try
        {
            PublishJournal(journal.Load());
        }
        catch (Exception error) when (IsJournalStorageError(error))
        {
            window.ShowJournalLoadError();
        }

        ReloadColorTimeline();
        sleepDetector.Rebase(SleepClockSample.Read());
        try
        {
            SystemEvents.PowerModeChanged += PowerModeChanged;
            watchingPower = true;
        }
        catch (InvalidOperationException error)
        {
            Trace.TraceWarning("Power notifications unavailable: {0}", error.Message);
        }
        timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timelineTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            if (!CheckSleep(now, SleepClockSample.Read()))
            {
                UpdateColorTimeline(now);
            }
        };
        timelineTimer.Start();
        backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        backupTimer.Tick += async (_, _) => await CheckAutomaticBackupAsync();
        backupTimer.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await CheckAutomaticBackupAsync()));

        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("打开主窗口", null, (_, _) => ShowMainWindow());
        trayMenu.Items.Add("退出", null, async (_, _) =>
        {
            exiting = true;
            await storageGate.WaitAsync();
            storageGate.Release();
            Shutdown();
        });
        using (var iconStream = GetResourceStream(new Uri("/BeaconLog;component/Assets/BeaconLog.ico", UriKind.Relative)).Stream)
        {
            trayArtwork = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        }

        trayIcon = new Forms.NotifyIcon
        {
            Icon = trayArtwork,
            Text = $"BeaconLog\n{FormatElapsedTime(displayedMinute ?? 0)}",
            ContextMenuStrip = trayMenu,
        };
        trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        trayIcon.Visible = true;

        if (!launchedAtLogin || !StartupSettings.SilentStartup)
        {
            window.Show();
        }

        if (StartupSettings.OpenFloatingWindowAtStartup)
        {
            ShowFloatingWindow();
        }

        restoreListener = ThreadPool.RegisterWaitForSingleObject(restoreRequest, (_, _) =>
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!exiting)
                {
                    ShowMainWindow();
                }
            });
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    internal void ShowBallInput(FrameworkElement ball)
    {
        if (inputWindow is { IsSaving: true } busy)
        {
            busy.Activate();
            return;
        }
        inputWindow?.Close();
        var draft = Stopwatch.GetElapsedTime(draftSavedAt) < DraftLifetime ? journalDraft : null;
        ClearDraft();
        var selected = draft is { } saved
            ? JournalCategories.FirstOrDefault(category => category.Id == saved.Category.Id) ?? JournalCategory.Ordinary
            : JournalCategory.Ordinary;
        OpenJournalInput(ball, draft?.Text ?? "", selected, AddJournalEntryAsync, isEdit: false);
    }

    internal StatusBallInputWindow ShowJournalEditor(FrameworkElement row, JournalEntry entry)
    {
        if (inputWindow is { IsSaving: true } busy)
        {
            busy.Activate();
            return busy;
        }
        inputWindow?.Close();
        return OpenJournalInput(row, entry.Text, entry.RecordCategory,
            (text, category) => EditJournalEntryAsync(entry, text, category), isEdit: true);
    }

    private StatusBallInputWindow OpenJournalInput(FrameworkElement anchor, string text, JournalCategory selected,
        Func<string, JournalCategory, Task<string?>> save, bool isEdit)
    {
        var input = new StatusBallInputWindow(anchor, text, JournalCategories, selected, save, isEdit);
        inputWindow = input;
        input.Closed += (_, _) =>
        {
            if (!isEdit && !input.WasSaved && input.UnsubmittedText.Length > 0)
            {
                RememberDraft(input.UnsubmittedText, input.SelectedCategory);
            }

            if (inputWindow == input)
            {
                inputWindow = null;
            }
        };
        input.Show();
        return input;
    }

    private void RememberDraft(string text, JournalCategory category)
    {
        journalDraft = (text, category);
        draftSavedAt = Stopwatch.GetTimestamp();
        if (draftTimer is null)
        {
            draftTimer = new DispatcherTimer { Interval = DraftLifetime };
            draftTimer.Tick += (_, _) => ClearDraft();
        }

        draftTimer.Stop();
        draftTimer.Start();
    }

    private void ClearDraft()
    {
        draftTimer?.Stop();
        journalDraft = null;
    }

    private async Task<string?> AddJournalEntryAsync(string text, JournalCategory category)
    {
        var now = DateTimeOffset.Now;
        var previousStart = startedAt;
        var elapsed = ElapsedMinutes(now);
        var appearance = StatusBallAppearance.Current;
        var color = StatusBallTimeline.Resolve(appearance.ColorHex, colorRules, elapsed);
        var entry = new JournalEntry(Guid.NewGuid(), text, now, $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            (long)Math.Max(0, (now - startedAt).TotalSeconds), category);
        JournalData data;
        try
        {
            data = await RunStorageAsync(() => journal.Add(entry));
        }
        catch (InvalidOperationException error)
        {
            return error.Message;
        }
        catch (Exception error) when (IsJournalStorageError(error))
        {
            return "未能保存记录，请重试。";
        }

        ClearDraft();
        if (startedAt == previousStart)
        {
            ResetTiming(now);
        }
        PublishJournal(data);
        return null;
    }

    private async Task<string?> EditJournalEntryAsync(JournalEntry original, string text, JournalCategory category)
    {
        JournalData data;
        try
        {
            data = await RunStorageAsync(() => journal.Edit(original, text, category));
        }
        catch (InvalidOperationException error)
        {
            return error.Message;
        }
        catch (Exception error) when (IsJournalStorageError(error))
        {
            return "未能保存修改，请重试。";
        }

        PublishJournal(data, data.Entries.First(entry => entry.Id == original.Id));
        return null;
    }

    internal Task<string?> DeleteJournalEntryAsync(Guid id) =>
        ChangeJournalAsync(store => store.Delete(id), "未能删除记录，请重试。");

    internal Task<string?> ChangeJournalCategoriesAsync(Func<JournalStore, JournalData> change) =>
        ChangeJournalAsync(change, "未能保存分类，请重试。");

    private async Task<string?> ChangeJournalAsync(Func<JournalStore, JournalData> change, string failure)
    {
        try
        {
            PublishJournal(await RunStorageAsync(() => change(journal)));
            return null;
        }
        catch (InvalidOperationException error)
        {
            return error.Message;
        }
        catch (Exception error) when (IsJournalStorageError(error))
        {
            return failure;
        }
    }

    private void PublishJournal(JournalData data, JournalEntry? edited = null)
    {
        JournalCategories = data.Categories;
        ((MainWindow)MainWindow).ShowJournal(data.Entries, edited);
    }

    internal async Task CheckAutomaticBackupAsync()
    {
        if (exiting || checkingBackup)
        {
            return;
        }

        checkingBackup = true;
        try
        {
            await RunStorageAsync(() => Backups.CheckAutomatic(DateTimeOffset.Now));
        }
        finally
        {
            checkingBackup = false;
        }
        if (MainWindow is MainWindow main)
        {
            foreach (var window in main.OwnedWindows.OfType<SettingsWindow>())
            {
                window.RefreshBackupStatus();
            }
        }
    }

    internal async Task<RestoreResult> RestoreBackupAsync(FullBackup backup, RestoreScope scope)
    {
        var result = await RunStorageAsync(() => Backups.Restore(backup, scope));
        var main = (MainWindow)MainWindow;
        if (result.Journal is { } data)
        {
            inputWindow?.Close();
            foreach (var editor in main.OwnedWindows.OfType<JournalCategoriesWindow>().ToArray())
            {
                editor.Close();
            }
            PublishJournal(data);
        }

        if (result.Settings is { } settings)
        {
            foreach (var editor in main.OwnedWindows.OfType<StatusBallSettingsWindow>().ToArray())
            {
                editor.Close();
            }
            StatusBallAppearance.Current.ApplyRestoredAppearance(settings.Diameter, settings.ColorHex, settings.InitialTitle);
            ReloadColorTimeline();
            floatingWindow?.ApplyRestoredPosition();
            ApplyFullscreenSetting(settings.HideWhenFullscreen);
            foreach (var window in main.OwnedWindows.OfType<SettingsWindow>().ToArray())
            {
                window.RefreshSettings();
            }
        }

        return result;
    }

    internal async Task<T> RunStorageAsync<T>(Func<T> operation)
    {
        await storageGate.WaitAsync();
        try
        {
            return await Task.Run(operation);
        }
        finally
        {
            storageGate.Release();
        }
    }

    internal Task RunStorageAsync(Action operation) => RunStorageAsync(() =>
    {
        operation();
        return true;
    });

    private static bool IsJournalStorageError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException or JsonException;

    internal void ReloadColorTimeline()
    {
        colorRules = StatusBallTimeline.Load();
        var now = DateTimeOffset.UtcNow;
        ResetReminderState(now);
        UpdateColorTimeline(now);
    }

    internal void ShowTimerResetConfirmation(FrameworkElement anchor, bool preferBeside = false)
    {
        if (!anchor.IsVisible || PresentationSource.FromVisual(anchor) is null)
        {
            return;
        }

        timerResetConfirmation?.Close();
        var panel = new TimerResetConfirmation(anchor, preferBeside, () => ResetTiming(DateTimeOffset.UtcNow));
        timerResetConfirmation = panel;
        panel.Closed += (_, _) => timerResetConfirmation = null;
        panel.Show();
    }

    internal void ResetTiming(DateTimeOffset now)
    {
        timerResetConfirmation?.Close();
        sleepDetector.Rebase(SleepClockSample.Read());
        startedAt = now;
        ResetReminderState(now);
        UpdateColorTimeline(now);
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            var sample = SleepClockSample.Read();
            _ = Dispatcher.InvokeAsync(() => CheckSleep(DateTimeOffset.UtcNow, sample));
        }
    }

    private bool CheckSleep(DateTimeOffset now, SleepClockSample? sample)
    {
        if (exiting || !sleepDetector.Observe(sample))
        {
            return false;
        }

        ResetTiming(now);
        return true;
    }

    private void ResetReminderState(DateTimeOffset now)
    {
        pendingReminder = null;
        lastReminderMinute = (long)ElapsedMinutes(now);
        lastTimelineTick = now;
    }

    private double ElapsedMinutes(DateTimeOffset now) => Math.Max(0, (now - startedAt).TotalMinutes);

    private static string FormatElapsedTime(long minute) => $"已经过 {minute} 分钟";

    private void UpdateElapsedDisplay(MainWindow main, long minute)
    {
        if (displayedMinute == minute)
        {
            return;
        }

        displayedMinute = minute;
        var text = FormatElapsedTime(minute);
        main.UpdateElapsedTime(text);
        if (trayIcon is { } icon)
        {
            icon.Text = $"BeaconLog\n{text}";
        }
    }

    private void UpdateColorTimeline(DateTimeOffset now)
    {
        var elapsed = ElapsedMinutes(now);
        var minute = (long)elapsed;
        var main = (MainWindow)MainWindow;
        UpdateElapsedDisplay(main, minute);
        main.UpdateJournalDate(now.ToLocalTime());
        var appearance = StatusBallAppearance.Current;
        appearance.ShowTimelineColor(StatusBallTimeline.Resolve(appearance.ColorHex, colorRules, elapsed));

        if (pendingReminder is { } pending && (elapsed < pending.Start || elapsed >= pending.End))
        {
            pendingReminder = null;
        }

        var tickGap = now - lastTimelineTick;
        lastTimelineTick = now;
        if (minute <= lastReminderMinute)
        {
            return;
        }

        var crossedOneMinute = minute == lastReminderMinute + 1;
        lastReminderMinute = minute;
        if (!crossedOneMinute || tickGap <= TimeSpan.Zero || tickGap > TimeSpan.FromSeconds(5)
            || StatusBallTimeline.ReminderSegment(colorRules, minute) is not { } segment)
        {
            return;
        }

        if (floatingWindow is { } window)
        {
            RefreshFloatingVisibility();
            if (window.IsVisible)
            {
                window.PlayLocationCue();
            }
            else if (elapsed < segment.End)
            {
                pendingReminder = segment;
            }
        }
        else if (main.IsVisible && main.WindowState != WindowState.Minimized)
        {
            main.PlayLocationCue();
        }
    }

    internal void ToggleFloatingWindow()
    {
        if (floatingWindow is null)
        {
            ShowFloatingWindow();
            return;
        }

        floatingWindow.Close();
    }

    private void ShowFloatingWindow()
    {
        var main = (MainWindow)MainWindow;
        floatingWindow = new FloatingStatusWindow(main);
        floatingWindow.Closed += (_, _) =>
        {
            fullscreenTimer?.Stop();
            floatingWindow = null;
            pendingReminder = null;
            main.UpdateFloatingStatus(opened: false, visible: false);
        };
        RefreshFloatingVisibility(playCue: true);
        UpdateFullscreenTimer();
    }

    internal void ApplyFullscreenSetting(bool enabled)
    {
        hideWhenFullscreen = enabled;
        if (!enabled)
        {
            pendingReminder = null;
        }
        RefreshFloatingVisibility();
        UpdateFullscreenTimer();
    }

    private void UpdateFullscreenTimer()
    {
        if (!hideWhenFullscreen || floatingWindow is null)
        {
            fullscreenTimer?.Stop();
            return;
        }

        if (fullscreenTimer is null)
        {
            fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            fullscreenTimer.Tick += (_, _) => RefreshFloatingVisibility();
        }

        fullscreenTimer.Start();
    }

    private void RefreshFloatingVisibility(bool playCue = false)
    {
        if (exiting || floatingWindow is not { } window)
        {
            return;
        }

        var hidden = hideWhenFullscreen && FullscreenDetector.IsFullscreenOn(window.CurrentScreen);
        if (hidden && window.IsVisible)
        {
            window.HideForFullscreen();
        }
        else if (!hidden && !window.IsVisible)
        {
            window.Show();
            var elapsed = ElapsedMinutes(DateTimeOffset.UtcNow);
            var deferred = pendingReminder is { } pending && elapsed >= pending.Start && elapsed < pending.End;
            pendingReminder = null;
            if (playCue || deferred)
            {
                window.PlayLocationCue();
            }
        }

        ((MainWindow)MainWindow).UpdateFloatingStatus(opened: true, visible: window.IsVisible);
    }

    private void ShowMainWindow()
    {
        if (!MainWindow.IsVisible)
        {
            MainWindow.Show();
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
    }

    private void HideMainWindow(object? sender, CancelEventArgs e)
    {
        if (exiting)
        {
            return;
        }

        e.Cancel = true;
        var window = (MainWindow)MainWindow;
        window.CloseAuxiliaryWindows();
        window.Hide();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        exiting = !e.Cancel;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        fullscreenTimer?.Stop();
        timelineTimer?.Stop();
        if (watchingPower)
        {
            SystemEvents.PowerModeChanged -= PowerModeChanged;
        }
        backupTimer?.Stop();
        ClearDraft();
        trayIcon?.Dispose();
        trayArtwork?.Dispose();
        trayMenu?.Dispose();
        restoreListener?.Unregister(null);
        restoreRequest?.Dispose();
        if (ownsInstance)
        {
            instanceMutex?.ReleaseMutex();
        }

        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

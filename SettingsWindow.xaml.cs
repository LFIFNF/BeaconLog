using System;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BeaconLog;

public partial class SettingsWindow : Window
{
    private readonly StatusBallAppearance appearance = StatusBallAppearance.Current;
    private bool ready;
    private App BeaconLogApp => (App)Application.Current;

    public SettingsWindow()
    {
        InitializeComponent();
        DiameterSlider.Minimum = StatusBallAppearance.MinimumDiameter;
        DiameterSlider.Maximum = StatusBallAppearance.MaximumDiameter;
        RefreshSettings();
    }

    internal void RefreshSettings()
    {
        ready = false;
        StartAtLoginOption.IsChecked = StartupSettings.StartAtLogin;
        SilentStartupOption.IsChecked = StartupSettings.SilentStartup;
        OpenFloatingWindowOption.IsChecked = StartupSettings.OpenFloatingWindowAtStartup;
        HideWhenFullscreenOption.IsChecked = StartupSettings.HideWhenFullscreen;
        AutoBackupOption.IsChecked = StartupSettings.AutomaticBackup;
        DiameterSlider.Value = appearance.Diameter;
        ready = true;
        RefreshBackupStatus();
    }

    private void DiameterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready)
        {
            return;
        }

        try
        {
            appearance.SaveDiameter((int)e.NewValue);
            SizeError.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            DiameterSlider.Value = appearance.Diameter;
            SizeError.Text = "无法保存大小设置。";
        }
    }

    private void SaveOption_Click(object sender, RoutedEventArgs e)
    {
        var option = (CheckBox)sender;
        var enabled = option.IsChecked == true;

        try
        {
            if (option == StartAtLoginOption)
            {
                StartupSettings.StartAtLogin = enabled;
            }
            else if (option == SilentStartupOption)
            {
                StartupSettings.SilentStartup = enabled;
            }
            else if (option == OpenFloatingWindowOption)
            {
                StartupSettings.OpenFloatingWindowAtStartup = enabled;
            }
            else if (option == HideWhenFullscreenOption)
            {
                StartupSettings.HideWhenFullscreen = enabled;
                BeaconLogApp.ApplyFullscreenSetting(enabled);
            }
            else
            {
                StartupSettings.AutomaticBackup = enabled;
                BeaconLogApp.CheckAutomaticBackup();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            option.IsChecked = !enabled;
            MessageBox.Show(this, "无法保存此设置。请检查当前账户权限后再试。",
                "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void RefreshBackupStatus()
    {
        var service = BeaconLogApp.Backups;
        var last = service.LastAutomaticBackup is { } timestamp
            ? $"最近自动备份：{timestamp:yyyy-MM-dd HH:mm}" : "暂无自动备份";
        BackupStatus.Text = service.AutomaticError is { } error ? $"{last} · {error}" : last;
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e) => SaveData(complete: false);
    private void BackupNow_Click(object sender, RoutedEventArgs e) => SaveData(complete: true);

    private void SaveData(bool complete)
    {
        var prefix = complete ? "BeaconLog-backup" : "BeaconLog-history";
        var dialog = new SaveFileDialog
        {
            Title = complete ? "保存完整备份" : "导出历史数据",
            DefaultExt = ".json",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (complete)
            {
                BeaconLogApp.Backups.SaveManualBackup(dialog.FileName);
            }
            else
            {
                BeaconLogApp.Backups.ExportHistory(dialog.FileName);
            }
            DataFeedback.Text = complete ? "完整备份已保存。" : "历史数据已导出。";
        }
        catch (Exception error) when (BackupService.IsDataError(error))
        {
            DataFeedback.Text = $"未能保存文件。{error.Message}";
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackupRestoreWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            DataFeedback.Text = $"恢复完成。恢复前副本已保存：{dialog.SafetyCopy}";
        }
    }
}

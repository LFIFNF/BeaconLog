using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BeaconLog;

public partial class BackupRestoreWindow : Window
{
    private sealed record BackupChoice(string Name, string FilePath);
    private FullBackup? selected;
    private int selectionGeneration;
    private bool restoring;
    private App BeaconLogApp => (App)Application.Current;
    internal string? SafetyCopy { get; private set; }

    public BackupRestoreWindow()
    {
        InitializeComponent();
        UpdateScopeDescription();
        try
        {
            LocalBackups.ItemsSource = BeaconLogApp.Backups.ListLocalBackups()
                .Select(path => new BackupChoice(Path.GetFileName(path), path)).ToArray();
            LocalBackups.SelectedIndex = LocalBackups.Items.Count == 0 ? -1 : 0;
            if (LocalBackups.Items.Count == 0)
            {
                Summary.Text = "暂无本地备份。可选择已保存的完整备份文件。";
            }
        }
        catch (Exception error) when (BackupService.IsDataError(error))
        {
            RestoreError.Text = "无法列出本地备份。仍可浏览文件。";
        }
    }

    private async void LocalBackups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalBackups.SelectedItem is BackupChoice choice)
        {
            await LoadBackupAsync(choice.FilePath);
        }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 BeaconLog 完整备份", Filter = "JSON 文件 (*.json)|*.json",
        };
        if (dialog.ShowDialog(this) == true)
        {
            LocalBackups.SelectedIndex = -1;
            await LoadBackupAsync(dialog.FileName);
        }
    }

    private async Task LoadBackupAsync(string path)
    {
        var generation = ++selectionGeneration;
        selected = null;
        RestoreButton.IsEnabled = false;
        RestoreError.Text = "";
        Summary.Text = Path.GetFileName(path);
        try
        {
            var backup = await BeaconLogApp.RunStorageAsync(() => BackupService.ReadBackup(path));
            if (generation != selectionGeneration)
            {
                return;
            }
            selected = backup;
            Summary.Text = $"{Path.GetFileName(path)}\n{selected.CreatedAt:yyyy-MM-dd HH:mm} · "
                + $"{selected.Journal.Entries.Count} 条记录 · {selected.Journal.Categories.Count} 个分类";
            if (selected.UnreadableHistory is not null)
            {
                Summary.Text = $"{Path.GetFileName(path)}\n{selected.CreatedAt:yyyy-MM-dd HH:mm} · 历史损坏，保留了原始文件内容";
                ScopeChoice.SelectedIndex = (int)RestoreScope.Settings;
            }
            RestoreButton.IsEnabled = true;
        }
        catch (Exception error) when (BackupService.IsDataError(error))
        {
            if (generation == selectionGeneration)
            {
                RestoreError.Text = $"无法读取备份。{error.Message}";
            }
        }
    }

    private RestoreScope Scope => (RestoreScope)Math.Max(ScopeChoice.SelectedIndex, 0);

    private string ScopeText => Scope switch
    {
        RestoreScope.History => "替换当前记录和分类。保留当前设置。",
        RestoreScope.Settings => "替换当前设置，包括开机自启、状态球外观、悬浮位置和自动备份。保留当前记录和分类。",
        _ => "替换当前记录、分类和全部设置，包括开机自启、悬浮位置和自动备份。",
    };

    private void ScopeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateScopeDescription();

    private void UpdateScopeDescription()
    {
        if (ScopeDescription is not null)
        {
            ScopeDescription.Text = ScopeText;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (selected is not { } backup || MessageBox.Show(this,
            $"{ScopeText}\n恢复前会另存当前数据。是否继续？", "确认恢复",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var scope = Scope;
        restoring = true;
        ((UIElement)Content).IsEnabled = false;
        try
        {
            SafetyCopy = (await BeaconLogApp.RestoreBackupAsync(backup, scope)).SafetyCopy;
        }
        catch (Exception error) when (BackupService.IsDataError(error))
        {
            RestoreError.Text = $"未能完成恢复。{error.Message}";
        }
        finally
        {
            restoring = false;
            ((UIElement)Content).IsEnabled = true;
        }
        if (SafetyCopy is not null)
        {
            DialogResult = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (restoring)
        {
            e.Cancel = true;
            return;
        }
        selectionGeneration++;
        base.OnClosing(e);
    }
}

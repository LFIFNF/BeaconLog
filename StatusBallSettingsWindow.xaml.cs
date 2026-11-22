using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;

namespace BeaconLog;

public partial class StatusBallSettingsWindow : Window
{
    private readonly StatusBallAppearance appearance = StatusBallAppearance.Current;
    private List<StatusBallColorRule> rules = [];
    private int editingIndex;

    public StatusBallSettingsWindow()
    {
        InitializeComponent();
        rules = StatusBallTimeline.Load();
        UpdateRuleList(-1);
    }

    private void UpdateRuleList(int selection)
    {
        RuleList.ItemsSource = new[] { new StatusBallColorRule(0, appearance.ColorHex, false, title: appearance.InitialTitle) }
            .Concat(rules).ToArray();
        RuleList.SelectedIndex = selection;
        if (selection >= 0)
        {
            RuleList.ScrollIntoView(RuleList.SelectedItem);
        }
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = RuleList.SelectedIndex;
        EditRule.IsEnabled = index >= 0;
        DeleteRule.IsEnabled = index > 0;
        MoveUp.IsEnabled = index > 1;
        MoveDown.IsEnabled = index > 0 && index < rules.Count;
    }

    private void AddRule_Click(object sender, RoutedEventArgs e) => OpenRuleEditor(-1);

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedIndex >= 0)
        {
            OpenRuleEditor(RuleList.SelectedIndex);
        }
    }

    private void OpenRuleEditor(int index)
    {
        editingIndex = index;
        var rule = index < 0 ? null : (StatusBallColorRule)RuleList.Items[index];
        var initial = index == 0;
        EditorTitle.Text = initial ? "编辑初始颜色" : index < 0 ? "添加规则" : $"编辑第 {index} 条规则";
        RuleTitle.Text = rule?.Title ?? "";
        RuleMinutes.Text = rule?.Minutes.ToString(CultureInfo.InvariantCulture) ?? "";
        RuleMinutes.IsReadOnly = initial;
        RuleColor.ColorText = rule?.ColorHex ?? appearance.ColorHex;
        RuleGradient.IsChecked = rule?.Gradient ?? false;
        RuleGradient.IsEnabled = !initial;
        RuleReminder.IsChecked = rule?.RippleReminder ?? false;
        ReminderMinutes.Text = (rule?.ReminderMinutes ?? StatusBallTimeline.DefaultReminderMinutes)
            .ToString(CultureInfo.InvariantCulture);
        ReminderFields.Visibility = initial ? Visibility.Collapsed : Visibility.Visible;
        EditorError.Text = "";
        RuleError.Text = "";
        RuleEditor.Visibility = Visibility.Visible;
        RuleTitle.Focus();
        RuleTitle.SelectAll();
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        var title = RuleTitle.Text.Trim();
        if (StringInfo.ParseCombiningCharacters(title).Length > 40)
        {
            EditorError.Text = "颜色标题最多 40 个字符。";
            return;
        }

        if (!StatusBallAppearance.TryReadRgb(RuleColor.ColorText, out _))
        {
            EditorError.Text = "请输入六位十六进制颜色，例如 #08A9C1。";
            return;
        }

        if (editingIndex == 0)
        {
            try
            {
                appearance.SaveInitialAppearance(RuleColor.ColorText, title);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                EditorError.Text = "无法保存初始颜色。";
                return;
            }

            ApplyChanges(0);
            return;
        }

        if (!int.TryParse(RuleMinutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes is < StatusBallTimeline.MinimumRuleMinutes or > StatusBallTimeline.MaximumRuleMinutes)
        {
            EditorError.Text = "请输入 10–99 分钟之间的整数时长。";
            return;
        }

        if (!int.TryParse(ReminderMinutes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var reminder)
            || reminder is < 1 or > 99)
        {
            EditorError.Text = "请输入 1–99 分钟之间的整数提醒间隔。";
            return;
        }

        var next = new List<StatusBallColorRule>(rules);
        var rule = new StatusBallColorRule(minutes, StatusBallAppearance.NormalizeHex(RuleColor.ColorText),
            RuleGradient.IsChecked == true, RuleReminder.IsChecked == true, reminder, title);
        var selection = editingIndex;
        if (selection < 0)
        {
            next.Add(rule);
            selection = next.Count;
        }
        else
        {
            next[selection - 1] = rule;
        }

        SaveRules(next, selection, EditorError);
    }

    private void CancelRule_Click(object sender, RoutedEventArgs e)
    {
        RuleEditor.Visibility = Visibility.Collapsed;
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        var index = RuleList.SelectedIndex;
        if (index <= 0)
        {
            return;
        }

        var next = new List<StatusBallColorRule>(rules);
        next.RemoveAt(index - 1);
        SaveRules(next, Math.Min(index, next.Count), RuleError);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveRule(1);

    private void MoveRule(int offset)
    {
        var index = RuleList.SelectedIndex;
        var target = index + offset;
        if (index <= 0 || target <= 0 || target > rules.Count)
        {
            return;
        }

        var next = new List<StatusBallColorRule>(rules);
        (next[index - 1], next[target - 1]) = (next[target - 1], next[index - 1]);
        SaveRules(next, target, RuleError);
    }

    private void SaveRules(List<StatusBallColorRule> next, int selection, TextBlock errorText)
    {
        try
        {
            StatusBallTimeline.Save(next);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            errorText.Text = "无法保存颜色规则。";
            return;
        }

        rules = next;
        ApplyChanges(selection);
    }

    private void ApplyChanges(int selection)
    {
        UpdateRuleList(selection);
        RuleError.Text = "";
        RuleEditor.Visibility = Visibility.Collapsed;
        ((App)Application.Current).ReloadColorTimeline();
    }
}

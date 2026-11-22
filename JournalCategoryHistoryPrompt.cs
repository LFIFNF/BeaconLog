using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeaconLog;

internal static class JournalCategoryHistoryPrompt
{
    internal static bool? Ask(Window owner, bool isDelete)
    {
        var dialog = new Window
        {
            Title = "是否应用于历史？",
            Owner = owner,
            Icon = owner.Icon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = 395,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Brushes.White,
            FontFamily = owner.FontFamily,
        };
        bool? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void AddChoice(string text, bool apply)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 0) };
            button.Click += (_, _) =>
            {
                result = apply;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(button);
        }

        AddChoice("应用于历史", true);
        AddChoice("仅影响以后", false);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Padding = new Thickness(10, 5, 10, 5) });
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = "是否应用于历史？",
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                },
                new TextBlock
                {
                    Text = isDelete
                        ? "应用后，已有记录将归入「普通」。仅影响以后时，已有记录保留原分类。"
                        : "应用后，已有记录也使用新名称。仅影响以后时，已有记录保留原名称。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 18),
                },
                buttons,
            },
        };
        dialog.ShowDialog();
        return result;
    }
}

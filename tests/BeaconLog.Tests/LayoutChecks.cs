using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeaconLog;

internal static class LayoutChecks
{
    internal static void Run()
    {
        var app = new App();
        app.InitializeComponent();
        var template = (DataTemplate)app.FindResource("JournalRowTemplate");
        var ordinary = new JournalEntry(Guid.NewGuid(), "每周计划 · Plan 42",
            new DateTimeOffset(2026, 11, 22, 9, 14, 0, TimeSpan.FromHours(8)), "#00A7C0", 128);
        var longRecord = ordinary with
        {
            Text = string.Concat(Enumerable.Repeat("连续的中文、English 123 和较长的正文。", 6)),
            Category = new JournalCategory(Guid.NewGuid(), "这是一个必须截断并允许悬停查看的很长的分类"),
        };

        foreach (var (width, scale) in new[] { (480, 1.0), (264, 1.5), (260, 2.0) })
        {
            var normal = Layout(template, ordinary, width, scale);
            var wrapped = Layout(template, longRecord, width, scale);
            Equal("普通", normal.Category.Text, "ordinary label");
            Equal(longRecord.RecordCategory.Name, wrapped.Category.ToolTip, "full category tooltip");
            Equal(TextTrimming.CharacterEllipsis, wrapped.Category.TextTrimming, "category trimming");
            Equal(ordinary.DurationText, normal.Time.ToolTip, "duration tooltip");

            foreach (var row in new[] { normal, wrapped })
            {
                Equal(row.Time.FontSize, row.Category.FontSize, "category font");
                Equal(row.Time.FontSize, row.Body.FontSize, "body font");
                Equal(row.Time.Foreground.ToString(), row.Category.Foreground.ToString(), "category color");
                Equal(row.Time.Foreground.ToString(), row.Body.Foreground.ToString(), "body color");
                Equal(row.Time.FontFamily, row.Body.FontFamily, "body family");
                Equal(row.Time.FontWeight, row.Category.FontWeight, "category weight");
                SamePosition(Baseline(row.Time, row.Grid), Baseline(row.Category, row.Grid), "category baseline");
                SamePosition(Baseline(row.Time, row.Grid), Baseline(row.Body, row.Grid), "body baseline");
            }

            SamePosition(Left(normal.Body, normal.Grid), Left(wrapped.Body, wrapped.Grid), "body column");
            SamePosition(Left(normal.Category, normal.Grid), Left(wrapped.Category, wrapped.Grid), "category column");
            if (wrapped.Body.ActualHeight <= wrapped.Body.LineHeight || wrapped.Body.ActualWidth <= 0)
            {
                throw new InvalidOperationException("Long text must wrap in the body column.");
            }
        }
    }

    private static (Grid Grid, TextBlock Time, TextBlock Category, TextBlock Body) Layout(
        DataTemplate template, JournalEntry entry, int width, double scale)
    {
        var row = (Grid)template.LoadContent();
        row.DataContext = entry;
        row.Measure(new Size(width, double.PositiveInfinity));
        row.Arrange(new Rect(0, 0, width, row.DesiredSize.Height));
        row.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(row.DesiredSize.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(row);
        return (row, (TextBlock)row.FindName("RecordTime"), (TextBlock)row.FindName("RecordCategory"),
            (TextBlock)row.FindName("RecordBody"));
    }

    private static double Left(FrameworkElement element, Visual root) =>
        element.TransformToAncestor(root).Transform(new Point()).X;

    private static double Baseline(TextBlock element, Visual root)
    {
        // WPF reports the last line's baseline. These rows use a fixed line height.
        var additionalLines = Math.Max(0, Math.Round(element.ActualHeight / element.LineHeight) - 1);
        var firstBaseline = element.BaselineOffset - additionalLines * element.LineHeight;
        return element.TransformToAncestor(root).Transform(new Point(0, firstBaseline)).Y;
    }

    private static void SamePosition(double expected, double actual, string operation)
    {
        if (!double.IsFinite(expected) || !double.IsFinite(actual) || Math.Abs(expected - actual) > 0.1)
        {
            throw new InvalidOperationException($"{operation}: expected {expected}, got {actual}");
        }
    }

    private static void Equal<T>(T expected, T actual, string operation)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"{operation}: expected {expected}, got {actual}");
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BeaconLog;

internal static class JournalListView
{
    internal static void ScrollToEnd(ListBox list)
    {
        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!list.IsVisible || list.Items.Count == 0)
            {
                return;
            }

            if (FindScrollViewer(list) is { } scroll)
            {
                scroll.ScrollToEnd();
            }
            else
            {
                list.ScrollIntoView(list.Items[^1]);
            }
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer scroll)
        {
            return scroll;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            var match = FindScrollViewer(VisualTreeHelper.GetChild(element, index));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}

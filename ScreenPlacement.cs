using System;
using System.Drawing;

namespace BeaconLog;

internal static class ScreenPlacement
{
    internal static Point Clamp(Point position, Size size, Rectangle workArea)
    {
        var maxX = Math.Max(workArea.Left, workArea.Right - size.Width);
        var maxY = Math.Max(workArea.Top, workArea.Bottom - size.Height);
        return new Point(Math.Clamp(position.X, workArea.Left, maxX),
            Math.Clamp(position.Y, workArea.Top, maxY));
    }
}

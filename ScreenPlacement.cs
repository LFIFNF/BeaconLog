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

    internal static Point Near(Rectangle anchor, Size size, Rectangle workArea, int gap, bool preferBeside)
    {
        var below = new Point(anchor.Left, anchor.Bottom + gap);
        var above = new Point(anchor.Left, anchor.Top - gap - size.Height);
        var right = new Point(anchor.Right + gap, anchor.Top + (anchor.Height - size.Height) / 2);
        var left = new Point(anchor.Left - gap - size.Width, right.Y);

        var candidates = preferBeside
            ? new[] { (right, true), (left, true), (below, false), (above, false) }
            : new[] { (below, false), (above, false), (right, true), (left, true) };
        foreach (var (position, horizontal) in candidates)
        {
            var fits = horizontal
                ? position.X >= workArea.Left && position.X + size.Width <= workArea.Right
                : position.Y >= workArea.Top && position.Y + size.Height <= workArea.Bottom;
            if (fits)
            {
                return Clamp(position, size, workArea);
            }
        }

        return Clamp(candidates[0].Item1, size, workArea);
    }
}

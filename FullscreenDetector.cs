using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace BeaconLog;

internal static class FullscreenDetector
{
    private const int Style = -16;
    private const int ExtendedStyle = -20;
    private const int Caption = 0x00C00000;
    private const int ToolWindow = 0x00000080;
    private const int ExtendedFrameBounds = 9;
    private const int Cloaked = 14;
    private const int Direct3DFullscreen = 3;

    internal static bool IsFullscreenOn(Forms.Screen screen)
    {
        var foreground = GetForegroundWindow();
        if (IsCandidateOnScreen(foreground, screen))
        {
            return (SHQueryUserNotificationState(out var notificationState) == 0
                && notificationState == Direct3DFullscreen)
                || CoversScreen(foreground, screen.Bounds);
        }

        // Focus can move to a different screen while a video stays full-screen here.
        var fullscreen = false;
        EnumWindows((window, _) =>
        {
            if (!IsCandidateOnScreen(window, screen))
            {
                return true;
            }

            fullscreen = CoversScreen(window, screen.Bounds);
            return false;
        }, IntPtr.Zero);
        return fullscreen;
    }

    private static bool IsCandidateOnScreen(IntPtr window, Forms.Screen screen)
    {
        if (window == IntPtr.Zero || window == GetShellWindow()
            || !IsWindowVisible(window) || IsIconic(window)
            || (GetWindowLong(window, ExtendedStyle) & ToolWindow) != 0)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == Environment.ProcessId
            || Forms.Screen.FromHandle(window).DeviceName != screen.DeviceName
            || (DwmGetWindowAttribute(window, Cloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
        {
            return false;
        }

        var className = new StringBuilder(64);
        GetClassName(window, className, className.Capacity);
        return className.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "WorkerW" or "Progman");
    }

    private static bool CoversScreen(IntPtr window, Rectangle screen)
    {
        if (IsZoomed(window) && (GetWindowLong(window, Style) & Caption) == Caption)
        {
            return false;
        }

        return DwmGetWindowAttribute(window, ExtendedFrameBounds, out WindowBounds bounds, 16) == 0
            && bounds.Left <= screen.Left && bounds.Top <= screen.Top
            && bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out WindowBounds value, int size);
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}

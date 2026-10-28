using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

internal readonly record struct WindowInfo(HWND Handle, Rectangle Bounds);

/// <summary>Visible top-level windows, front to back, with their visible (shadow-free) bounds.</summary>
internal static unsafe class WindowEnumerator
{
    private static List<WindowInfo>? s_list;
    private static uint s_ownPid;

    public static List<WindowInfo> GetVisibleWindows()
    {
        s_list = new List<WindowInfo>();
        s_ownPid = (uint)Environment.ProcessId;
        PInvoke.EnumWindows(&EnumProc, 0);
        var result = s_list;
        s_list = null;
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static BOOL EnumProc(HWND hwnd, LPARAM data)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) return true;

        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == s_ownPid) return true;

        uint exStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0) return true;

        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        if (cloaked != 0) return true; // e.g. UWP windows on other virtual desktops

        if (GetFrameBounds(hwnd) is { Width: > 1, Height: > 1 } rect) s_list?.Add(new WindowInfo(hwnd, rect));
        return true;
    }

    /// <summary>The window's visible bounds (without the drop shadow / invisible resize border).</summary>
    public static Rectangle? GetFrameBounds(HWND hwnd)
    {
        RECT r;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Failed)
            return null;
        return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
    }
}

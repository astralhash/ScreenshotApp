using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>Visible top-level windows, front to back, with their visible (shadow-free) bounds.</summary>
internal static unsafe class WindowEnumerator
{
    private static List<Rectangle>? s_list;
    private static uint s_ownPid;

    public static List<Rectangle> GetVisibleWindowBounds()
    {
        s_list = new List<Rectangle>();
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

        RECT r;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Failed)
            return true;
        var rect = Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        if (rect.Width > 1 && rect.Height > 1) s_list?.Add(rect);
        return true;
    }
}

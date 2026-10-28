using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>Monitor and virtual-screen geometry in physical pixels.</summary>
internal static unsafe class Monitors
{
    private static List<Rectangle>? s_list;

    public static Rectangle VirtualScreen => new(
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
        PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    public static List<Rectangle> GetAll()
    {
        s_list = new List<Rectangle>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, &EnumProc, 0);
        var result = s_list;
        s_list = null;
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static BOOL EnumProc(HMONITOR monitor, HDC hdc, RECT* rect, LPARAM data)
    {
        s_list?.Add(Rectangle.FromLTRB(rect->left, rect->top, rect->right, rect->bottom));
        return true;
    }
}

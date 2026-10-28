using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// A thin coloured frame drawn just outside a live region (so it is never in the region's own frames), made of
/// four topmost, click-through Win32 strips.
/// </summary>
internal sealed unsafe class RegionOutline : IDisposable
{
    private const string ClassName = "ScreenshotApp.RegionOutline";
    private const int Thickness = 3; // physical pixels

    private static bool s_classRegistered;
    private static HBRUSH s_brush;

    private readonly HWND[] _strips = new HWND[4];

    public RegionOutline(Rectangle region)
    {
        EnsureClass();
        int t = Thickness;
        Rectangle[] rects =
        [
            new(region.X - t, region.Y - t, region.Width + 2 * t, t), // top
            new(region.X - t, region.Bottom, region.Width + 2 * t, t), // bottom
            new(region.X - t, region.Y, t, region.Height),             // left
            new(region.Right, region.Y, t, region.Height),             // right
        ];
        var hInstance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        for (int i = 0; i < 4; i++)
        {
            fixed (char* cls = ClassName)
            {
                _strips[i] = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                    | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                    cls, cls, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, HMENU.Null, hInstance, null);
            }
            if (_strips[i] == HWND.Null) continue;
            // Layered + transparent makes the strip click-through; it still needs an opacity to be drawn at all.
            PInvoke.SetLayeredWindowAttributes(_strips[i], new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
            var r = rects[i];
            PInvoke.SetWindowPos(_strips[i], new HWND(-1), r.X, r.Y, r.Width, r.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
    }

    public void Show(bool visible)
    {
        foreach (var s in _strips)
            if (s != HWND.Null) PInvoke.ShowWindow(s, visible ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE);
    }

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
        s_brush = PInvoke.CreateSolidBrush(new COLORREF(0x002311E8)); // #E81123
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &StaticWndProc,
                hInstance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value,
                hbrBackground = s_brush,
                lpszClassName = cls,
            };
            if (PInvoke.RegisterClassEx(wc) == 0) throw new InvalidOperationException("RegisterClassEx failed.");
        }
        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        for (int i = 0; i < _strips.Length; i++)
        {
            if (_strips[i] == HWND.Null) continue;
            PInvoke.DestroyWindow(_strips[i]);
            _strips[i] = HWND.Null;
        }
    }
}

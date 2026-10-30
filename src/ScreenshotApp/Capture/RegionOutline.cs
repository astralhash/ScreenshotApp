using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// A thin coloured frame around a live region, made of four topmost, click-through Win32 strips. Each side sits
/// just outside the region, or just inside it where the outside would be off-screen (region on a screen edge).
/// The strips are excluded from screen capture, so they are never in the shared frames or in screenshots.
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
            // A side drawn inside the region would otherwise show in the shared frames.
            if (!PInvoke.SetWindowDisplayAffinity(_strips[i], WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
                Services.Log.Info($"RegionOutline: SetWindowDisplayAffinity failed ({Marshal.GetLastWin32Error()}).");
        }
        SetRegion(region);
    }

    /// <summary>Moves the frame to <paramref name="region"/> (visibility unchanged).</summary>
    public void SetRegion(Rectangle region)
    {
        int t = Thickness;
        var monitors = Monitors.GetAll();
        // The frame's outer box: each side moves inward when the strip outside the region would not be on screen.
        int left = OnScreen(new(region.X - t, region.Y, t, region.Height), monitors) ? region.X - t : region.X;
        int right = OnScreen(new(region.Right, region.Y, t, region.Height), monitors) ? region.Right + t : region.Right;
        int top = OnScreen(new(region.X, region.Y - t, region.Width, t), monitors) ? region.Y - t : region.Y;
        int bottom = OnScreen(new(region.X, region.Bottom, region.Width, t), monitors) ? region.Bottom + t : region.Bottom;
        int innerHeight = Math.Max(0, bottom - top - 2 * t);
        Rectangle[] rects =
        [
            new(left, top, right - left, t),               // top
            new(left, bottom - t, right - left, t),        // bottom
            new(left, top + t, t, innerHeight),            // left
            new(right - t, top + t, t, innerHeight),       // right
        ];
        for (int i = 0; i < 4; i++)
        {
            if (_strips[i] == HWND.Null) continue;
            var r = rects[i];
            PInvoke.SetWindowPos(_strips[i], new HWND(-1), r.X, r.Y, r.Width, r.Height,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }
    }

    /// <summary>True when <paramref name="strip"/> lies entirely on monitors (they never overlap).</summary>
    private static bool OnScreen(Rectangle strip, List<Rectangle> monitors)
    {
        long covered = 0;
        foreach (var m in monitors)
        {
            var r = Rectangle.Intersect(strip, m);
            if (!r.IsEmpty) covered += (long)r.Width * r.Height;
        }
        return covered == (long)strip.Width * strip.Height;
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

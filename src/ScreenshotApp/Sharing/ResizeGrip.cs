using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Sharing;

/// <summary>
/// Makes a shared region's frame resizable like a window edge: an invisible, ring-shaped topmost window over the frame
/// (a few pixels to either side) that turns the cursor into the matching resize arrows and drags the hovered edge, or
/// both edges at a corner. It is excluded from screen capture. While dragging it reports the region it would have
/// through <see cref="RegionDragging"/> and, on release, <see cref="RegionDropped"/>.
/// </summary>
internal sealed unsafe class ResizeGrip : IDisposable
{
    private const string ClassName = "ScreenshotApp.ResizeGrip";
    private const int MinRegion = 48; // physical pixels

    private static readonly Dictionary<nint, ResizeGrip> Windows = new();
    private static bool s_classRegistered;

    private HWND _hwnd;
    private Rectangle _region;
    private Edges _dragZone;
    private Point _dragStart;
    private Rectangle _dragRegion, _dragMonitor;

    public bool Visible { get; private set; }

    public bool IsDragging { get; private set; }

    /// <summary>An edge is being dragged; this is the region as it would be now.</summary>
    public event Action<Rectangle>? RegionDragging;

    /// <summary>The edge was released; this is the region to switch to.</summary>
    public event Action<Rectangle>? RegionDropped;

    public ResizeGrip()
    {
        EnsureClass();
        fixed (char* cls = ClassName)
        {
            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                | WINDOW_EX_STYLE.WS_EX_LAYERED,
                cls, cls, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, HMENU.Null,
                (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value, null);
        }
        if (_hwnd == HWND.Null) throw new InvalidOperationException("Grip CreateWindowEx failed.");
        Windows[_hwnd] = this;
        // Practically invisible but not fully transparent: a fully transparent layered window can be clicked through.
        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Puts the grip on the frame of <paramref name="region"/> (or moves it there) and makes it hittable.</summary>
    public void Show(Rectangle region)
    {
        _region = region;
        int b = ShareToolbar.EdgeBand;
        var outer = Rectangle.Inflate(region, b, b);

        // The window is the frame's bounding box with the middle cut out, so only the ring takes the mouse.
        var ring = PInvoke.CreateRectRgn(0, 0, outer.Width, outer.Height);
        if (region.Width > 2 * b && region.Height > 2 * b)
        {
            var hole = PInvoke.CreateRectRgn(2 * b, 2 * b, outer.Width - 2 * b, outer.Height - 2 * b);
            PInvoke.CombineRgn(ring, ring, hole, RGN_COMBINE_MODE.RGN_DIFF);
            PInvoke.DeleteObject(hole);
        }
        PInvoke.SetWindowRgn(_hwnd, ring, true); // the system owns the region now
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), outer.X, outer.Y, outer.Width, outer.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | (Visible ? SET_WINDOW_POS_FLAGS.SWP_NOZORDER : SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW));
        Visible = true;

        // The cursor only changes on its next move otherwise; show the arrows right away.
        var zone = ShareToolbar.ZoneAt(region, CursorPos());
        if (zone != Edges.None) PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, CursorFor(zone)));
    }

    public void Hide()
    {
        if (!Visible || IsDragging) return;
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        Visible = false;
    }

    private static PCWSTR CursorFor(Edges zone) => zone switch
    {
        Edges.Left or Edges.Right => PInvoke.IDC_SIZEWE,
        Edges.Top or Edges.Bottom => PInvoke.IDC_SIZENS,
        _ when (zone.HasFlag(Edges.Left) && zone.HasFlag(Edges.Top)) || (zone.HasFlag(Edges.Right) && zone.HasFlag(Edges.Bottom))
            => PInvoke.IDC_SIZENWSE,
        _ => PInvoke.IDC_SIZENESW,
    };

    private static void EnsureClass()
    {
        if (s_classRegistered) return;
        fixed (char* cls = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &StaticWndProc,
                hInstance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value,
                lpszClassName = cls,
            };
            if (PInvoke.RegisterClassEx(wc) == 0) throw new InvalidOperationException("RegisterClassEx failed.");
        }
        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam) =>
        Windows.TryGetValue(hwnd, out var w)
            ? w.WndProc(hwnd, msg, wParam, lParam)
            : PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
            case PInvoke.WM_SETCURSOR:
            {
                var zone = IsDragging ? _dragZone : ShareToolbar.ZoneAt(_region, CursorPos());
                PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, zone == Edges.None ? PInvoke.IDC_ARROW : CursorFor(zone)));
                return (LRESULT)1;
            }
            case PInvoke.WM_LBUTTONDOWN:
            {
                var cursor = CursorPos();
                var zone = ShareToolbar.ZoneAt(_region, cursor);
                if (zone == Edges.None) return (LRESULT)0;
                PInvoke.SetCapture(hwnd);
                IsDragging = true;
                _dragZone = zone;
                _dragStart = cursor;
                _dragRegion = _region;
                _dragMonitor = ShareToolbar.MonitorOf(_region);
                return (LRESULT)0;
            }
            case PInvoke.WM_MOUSEMOVE:
                if (IsDragging)
                {
                    var cursor = CursorPos();
                    var r = Resized(_dragRegion, _dragMonitor, _dragZone, cursor.X - _dragStart.X, cursor.Y - _dragStart.Y);
                    if (r != _region)
                    {
                        _region = r;
                        RegionDragging?.Invoke(r);
                    }
                }
                return (LRESULT)0;
            case PInvoke.WM_LBUTTONUP:
                PInvoke.ReleaseCapture();
                if (IsDragging)
                {
                    IsDragging = false;
                    RegionDropped?.Invoke(_region);
                }
                return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>The region after dragging the <paramref name="zone"/> side(s) by (dx, dy), kept on its monitor and not too small.</summary>
    private static Rectangle Resized(Rectangle start, Rectangle monitor, Edges zone, int dx, int dy)
    {
        int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        if (zone.HasFlag(Edges.Left)) l = ShareToolbar.Clamp(l + dx, monitor.Left, r - MinRegion);
        if (zone.HasFlag(Edges.Right)) r = ShareToolbar.Clamp(r + dx, l + MinRegion, monitor.Right);
        if (zone.HasFlag(Edges.Top)) t = ShareToolbar.Clamp(t + dy, monitor.Top, b - MinRegion);
        if (zone.HasFlag(Edges.Bottom)) b = ShareToolbar.Clamp(b + dy, t + MinRegion, monitor.Bottom);
        return Rectangle.FromLTRB(l, t, r, b);
    }

    private static Point CursorPos()
    {
        PInvoke.GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    public void Dispose()
    {
        if (_hwnd == HWND.Null) return;
        Windows.Remove(_hwnd);
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = HWND.Null;
    }
}

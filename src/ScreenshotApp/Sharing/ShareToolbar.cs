using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ScreenshotApp.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Sharing;

/// <summary>The sides of a region's frame the cursor is on; two sides (one horizontal, one vertical) make a corner.</summary>
[Flags]
internal enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>
/// The small dark toolbar next to a shared region's frame, right where the cursor touched it: a move handle and an X
/// that ends the share. A topmost Win32 window that does not take focus and is excluded from screen capture.
/// Dragging the move handle reports the region it would have through <see cref="RegionDragging"/> and, on release,
/// <see cref="RegionDropped"/>. (Resizing is done on the frame itself, see <see cref="ResizeGrip"/>.)
/// </summary>
internal sealed unsafe class ShareToolbar : IDisposable
{
    private const string ClassName = "ScreenshotApp.ShareToolbar";
    private const int Move = 0, Close = 1, ButtonCount = 2;
    private const int Button = 30, Pad = 3, Gap = 2, Height = 36, Radius = 8; // device-independent pixels
    private const int FrameThickness = 3;                                     // physical pixels
    internal const int EdgeBand = 8, CornerZone = 16;                         // physical pixels: hover reach of an edge / of a corner
    private const uint Background = 0x002B2B2B, Highlight = 0x00454545, CloseHighlight = 0x002311E8, Glyph = 0x00FFFFFF;

    private static readonly Dictionary<nint, ShareToolbar> Windows = new();
    private static bool s_classRegistered;

    private HWND _hwnd;
    private Rectangle _bounds;          // the window, in virtual-screen physical pixels
    private Rectangle _region;          // the region the toolbar belongs to (follows a drag)
    private double _scale = 1;
    private int _hot = -1, _pressed = -1;
    private bool _vertical;            // placed beside the frame: the buttons are stacked
    private bool _dragging;            // the move handle is being dragged
    private Point _dragStart;
    private Rectangle _dragRegion, _dragMonitor, _dragBounds;
    private Point _anchor;

    public bool Visible { get; private set; }

    /// <summary>The frame side(s) the toolbar was placed against, which decides where it sits.</summary>
    public Edges Zone { get; private set; }

    /// <summary>Where the cursor was on the frame when the toolbar was placed.</summary>
    public Point Anchor => _anchor;

    /// <summary>The frame sides under <paramref name="cursor"/> (within a few pixels), or None.</summary>
    public static Edges ZoneAt(Rectangle region, Point cursor)
    {
        int b = EdgeBand;
        bool inX = cursor.X >= region.Left - b && cursor.X <= region.Right + b;
        bool inY = cursor.Y >= region.Top - b && cursor.Y <= region.Bottom + b;
        int dl = Math.Abs(cursor.X - region.Left), dr = Math.Abs(cursor.X - region.Right);
        int dt = Math.Abs(cursor.Y - region.Top), db = Math.Abs(cursor.Y - region.Bottom);

        var z = Edges.None;
        if (inY && Math.Min(dl, dr) <= b) z |= dl <= dr ? Edges.Left : Edges.Right;
        if (inX && Math.Min(dt, db) <= b) z |= dt <= db ? Edges.Top : Edges.Bottom;
        // Near a corner along an edge: the corner, so both dimensions can be dragged from there.
        if (z is Edges.Left or Edges.Right && Math.Min(dt, db) <= CornerZone) z |= dt <= db ? Edges.Top : Edges.Bottom;
        if (z is Edges.Top or Edges.Bottom && Math.Min(dl, dr) <= CornerZone) z |= dl <= dr ? Edges.Left : Edges.Right;
        return z;
    }

    public bool IsDragging => _dragging;

    /// <summary>The X was clicked.</summary>
    public event Action? CloseRequested;

    /// <summary>A handle is being dragged; this is the region as it would be now.</summary>
    public event Action<Rectangle>? RegionDragging;

    /// <summary>The handle was released; this is the region to switch to.</summary>
    public event Action<Rectangle>? RegionDropped;

    public ShareToolbar()
    {
        EnsureClass();
        fixed (char* cls = ClassName)
        {
            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                cls, cls, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, HMENU.Null,
                (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value, null);
        }
        if (_hwnd == HWND.Null) throw new InvalidOperationException("Toolbar CreateWindowEx failed.");
        Windows[_hwnd] = this;
        // The toolbar sits over the shared area at times; keep it out of the shared frames and screenshots.
        PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
    }

    public bool Contains(Point screenPoint) => Visible && _bounds.Contains(screenPoint);

    /// <summary>
    /// Shows the toolbar against the frame at <paramref name="anchor"/> (where the cursor is): above or below for a
    /// top/bottom edge, beside it for a left/right edge, on the other side or inside when there is no room.
    /// </summary>
    public void Place(Rectangle region, Point anchor, Edges zone)
    {
        _anchor = anchor;
        Zone = zone == Edges.None ? Edges.Bottom : zone;
        // Land on the cursor's monitor first so the window's DPI, and so the toolbar's size, is the right one.
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), anchor.X, anchor.Y, 1, 1, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _scale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
        Layout(region);
    }

    /// <summary>Puts the toolbar back on top of the other topmost windows (the resize ring).</summary>
    public void Raise()
    {
        if (!Visible) return;
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
    }

    public void Hide()
    {
        if (!Visible || IsDragging) return;
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        Visible = false;
        _hot = _pressed = -1;
    }

    /// <summary>Updates the hover highlight; called with the cursor position while the toolbar is up.</summary>
    public void Refresh(Point cursor)
    {
        if (!Visible) return;
        int hot = HitTest(cursor);
        if (hot == _hot) return;
        _hot = hot;
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    private int Px(double value) => (int)Math.Round(value * _scale);

    private void Layout(Rectangle region)
    {
        _region = region;
        int length = Px(2 * Pad + ButtonCount * Button + (ButtonCount - 1) * Gap), thickness = Px(Height);
        var mon = MonitorOf(region);
        int f = FrameThickness;
        // Above or below the frame the buttons sit side by side; beside it they are stacked, so the toolbar stays narrow.
        // The move button is right at the cursor (the X next to it), so moving onto the toolbar lands on "move".
        int toMove = Px(Pad) + Px(Button) / 2;
        int cx = Clamp(_anchor.X - toMove, mon.Left, mon.Right - length), cy = Clamp(_anchor.Y - toMove, mon.Top, mon.Bottom - length);
        var above = new Rectangle(cx, region.Top - f - thickness, length, thickness);
        var below = new Rectangle(cx, region.Bottom + f, length, thickness);
        var left = new Rectangle(region.Left - f - thickness, cy, thickness, length);
        var right = new Rectangle(region.Right + f, cy, thickness, length);
        Rectangle[] order = Zone.HasFlag(Edges.Top) ? [above, below]
            : Zone.HasFlag(Edges.Bottom) ? [below, above]
            : Zone.HasFlag(Edges.Left) ? [left, right, above, below]
            : [right, left, above, below];
        // No room outside the frame: inside it, at the cursor.
        var placed = order.FirstOrDefault(c => mon.Contains(c));
        if (placed.IsEmpty)
            placed = new Rectangle(cx, Clamp(_anchor.Y - Px(Pad) - Px(Button) / 2, region.Top, region.Bottom - thickness), length, thickness);
        int x = placed.X, y = placed.Y, w = placed.Width, h = placed.Height;
        _vertical = h > w;

        bool resized = w != _bounds.Width || h != _bounds.Height;
        _bounds = new Rectangle(x, y, w, h);
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), x, y, w, h,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | (Visible ? 0 : SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW));
        if (resized || !Visible)
        {
            int r = Px(Radius);
            PInvoke.SetWindowRgn(_hwnd, PInvoke.CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2), true); // the system owns the region
        }
        Visible = true;
    }

    internal static Rectangle MonitorOf(Rectangle region)
    {
        var centre = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        foreach (var m in Monitors.GetAll())
            if (m.Contains(centre)) return m;
        return Monitors.VirtualScreen;
    }

    private Rectangle ButtonRect(int index)
    {
        int size = Px(Button), offset = Px(Pad) + index * (size + Px(Gap));
        return _vertical ? new Rectangle(Px(Pad), offset, size, size) : new Rectangle(offset, Px(Pad), size, size);
    }

    /// <summary>The button under a screen point, or -1.</summary>
    private int HitTest(Point screen)
    {
        var local = new Point(screen.X - _bounds.X, screen.Y - _bounds.Y);
        for (int i = 0; i < ButtonCount; i++)
            if (ButtonRect(i).Contains(local)) return i;
        return -1;
    }

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
            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;
            case PInvoke.WM_PAINT:
                Paint();
                return (LRESULT)0;
            case PInvoke.WM_SETCURSOR:
                PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null,
                    HitTest(CursorPos()) == Move ? PInvoke.IDC_SIZEALL : PInvoke.IDC_ARROW));
                return (LRESULT)1;
            case PInvoke.WM_MOUSEMOVE:
                OnMouseMove();
                return (LRESULT)0;
            case PInvoke.WM_LBUTTONDOWN:
                OnMouseDown(hwnd);
                return (LRESULT)0;
            case PInvoke.WM_LBUTTONUP:
                OnMouseUp();
                return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static Point CursorPos()
    {
        PInvoke.GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    private void OnMouseDown(HWND hwnd)
    {
        var cursor = CursorPos();
        int hit = HitTest(cursor);
        if (hit < 0) return;
        _pressed = hit;
        PInvoke.SetCapture(hwnd);
        if (hit == Move)
        {
            _dragging = true;
            _dragStart = cursor;
            _dragRegion = _region;
            _dragMonitor = MonitorOf(_region);
            _dragBounds = _bounds;
        }
        PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
    }

    private void OnMouseMove()
    {
        var cursor = CursorPos();
        if (IsDragging)
        {
            int dx = cursor.X - _dragStart.X, dy = cursor.Y - _dragStart.Y;
            var s = _dragRegion;
            var m = _dragMonitor;
            // The toolbar goes along with the region, so the cursor stays on its handle.
            var r = new Rectangle(Clamp(s.X + dx, m.Left, m.Right - s.Width), Clamp(s.Y + dy, m.Top, m.Bottom - s.Height), s.Width, s.Height);
            if (r != _region)
            {
                _region = r;
                _bounds = new Rectangle(_dragBounds.X + r.X - s.X, _dragBounds.Y + r.Y - s.Y, _dragBounds.Width, _dragBounds.Height);
                PInvoke.SetWindowPos(_hwnd, new HWND(-1), _bounds.X, _bounds.Y, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
                RegionDragging?.Invoke(r);
            }
        }
        Refresh(cursor);
    }

    private void OnMouseUp()
    {
        PInvoke.ReleaseCapture();
        var cursor = CursorPos();
        int pressed = _pressed;
        _pressed = -1;
        if (IsDragging)
        {
            _dragging = false;
            RegionDropped?.Invoke(_region);
        }
        else if (pressed == Close && HitTest(cursor) == Close)
        {
            CloseRequested?.Invoke();
        }
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    /// <summary>Like Math.Clamp, but the minimum wins when the range is empty.</summary>
    internal static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));

    private void Paint()
    {
        var hdc = PInvoke.BeginPaint(_hwnd, out var ps);
        try
        {
            int w = _bounds.Width, h = _bounds.Height;
            var mem = PInvoke.CreateCompatibleDC(hdc);
            var bmp = PInvoke.CreateCompatibleBitmap(hdc, w, h);
            var oldBmp = PInvoke.SelectObject(mem, bmp);

            Fill(mem, new RECT { right = w, bottom = h }, Background);
            int lit = _pressed >= 0 ? _pressed : _hot;
            for (int i = 0; i < ButtonCount; i++)
            {
                var r = ButtonRect(i);
                if (i == lit)
                    Fill(mem, new RECT { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom }, i == Close ? CloseHighlight : Highlight);
                DrawGlyph(mem, i, r);
            }

            PInvoke.BitBlt(hdc, 0, 0, w, h, mem, 0, 0, ROP_CODE.SRCCOPY);
            PInvoke.SelectObject(mem, oldBmp);
            PInvoke.DeleteObject(bmp);
            PInvoke.DeleteDC(mem);
        }
        finally
        {
            PInvoke.EndPaint(_hwnd, ps);
        }
    }

    private static void Fill(HDC hdc, RECT rect, uint color)
    {
        var brush = PInvoke.CreateSolidBrush(new COLORREF(color));
        PInvoke.FillRect(hdc, rect, brush);
        PInvoke.DeleteObject(brush);
    }

    private void DrawGlyph(HDC hdc, int index, Rectangle button)
    {
        int cx = button.X + button.Width / 2, cy = button.Y + button.Height / 2;
        var pen = PInvoke.CreatePen(PEN_STYLE.PS_SOLID, Math.Max(2, Px(1.5)), new COLORREF(Glyph));
        var oldPen = PInvoke.SelectObject(hdc, pen);
        switch (index)
        {
            case Move: // four arrows pointing outwards
            {
                int a = Px(7), d = Px(3);
                Line(hdc, cx - a, cy, cx + a, cy);
                Line(hdc, cx, cy - a, cx, cy + a);
                Line(hdc, cx + a, cy, cx + a - d, cy - d); Line(hdc, cx + a, cy, cx + a - d, cy + d);
                Line(hdc, cx - a, cy, cx - a + d, cy - d); Line(hdc, cx - a, cy, cx - a + d, cy + d);
                Line(hdc, cx, cy + a, cx - d, cy + a - d); Line(hdc, cx, cy + a, cx + d, cy + a - d);
                Line(hdc, cx, cy - a, cx - d, cy - a + d); Line(hdc, cx, cy - a, cx + d, cy - a + d);
                break;
            }
            default: // X
            {
                int a = Px(5);
                Line(hdc, cx - a, cy - a, cx + a + 1, cy + a + 1);
                Line(hdc, cx - a, cy + a, cx + a + 1, cy - a - 1);
                break;
            }
        }
        PInvoke.SelectObject(hdc, oldPen);
        PInvoke.DeleteObject(pen);
    }

    private static void Line(HDC hdc, int x1, int y1, int x2, int y2)
    {
        PInvoke.MoveToEx(hdc, x1, y1, null);
        PInvoke.LineTo(hdc, x2, y2);
    }

    public void Dispose()
    {
        if (_hwnd == HWND.Null) return;
        Windows.Remove(_hwnd);
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = HWND.Null;
    }
}

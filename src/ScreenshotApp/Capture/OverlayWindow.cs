using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// One borderless topmost Win32 window covering a single monitor. Paints the frozen (dimmed) screen,
/// the selection at full brightness, a crosshair and a size label, all with GDI double-buffering.
/// </summary>
internal sealed unsafe class OverlayWindow : IDisposable
{
    private const string ClassName = "ScreenshotApp.CaptureOverlay";
    private const uint EscTimerId = 1;

    private static readonly Dictionary<nint, OverlayWindow> Windows = new();
    private static bool s_classRegistered;

    private readonly CaptureOverlay _owner;
    private readonly Rectangle _bounds; // monitor rect in virtual-screen physical pixels
    private HBITMAP _dimBmp, _brightBmp, _backBmp;
    private HDC _dimDc, _brightDc, _backDc;
    private HFONT _font;
    private double _scale = 1;
    private void* _brightBits; // pixels of the bright (undimmed) bitmap, BGRA, for the lens color readout

    public HWND Handle { get; private set; }

    public OverlayWindow(CaptureOverlay owner, Rectangle monitorBounds, CapturedImage frozen, bool pollEsc)
    {
        _owner = owner;
        _bounds = monitorBounds;
        EnsureClass();
        BuildBitmaps(frozen);

        fixed (char* cls = ClassName)
        {
            Handle = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                cls, cls, WINDOW_STYLE.WS_POPUP,
                _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
                HWND.Null, HMENU.Null, (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value, null);
        }
        if (Handle == HWND.Null) throw new InvalidOperationException("Overlay CreateWindowEx failed.");

        Windows[Handle] = this;
        // Re-apply bounds: with per-monitor DPI the window may have been scaled while being created.
        PInvoke.SetWindowPos(Handle, new HWND(-1), _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

        _scale = PInvoke.GetDpiForWindow(Handle) / 96.0;
        _font = PInvoke.CreateFont(-(int)Math.Round(14 * _scale), 0, 0, 0, 600, 0, 0, 0, FONT_CHARSET.DEFAULT_CHARSET, 0, 0, FONT_QUALITY.CLEARTYPE_QUALITY, 0, "Segoe UI");

        // Esc fallback in case the overlay never receives keyboard focus.
        if (pollEsc) PInvoke.SetTimer(Handle, EscTimerId, 30, null);
    }

    public void Activate() => PInvoke.SetForegroundWindow(Handle);

    public void Invalidate() => PInvoke.InvalidateRect(Handle, (RECT*)null, false);

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

    private void BuildBitmaps(CapturedImage frozen)
    {
        var screenDc = PInvoke.GetDC(HWND.Null);
        try
        {
            _dimDc = PInvoke.CreateCompatibleDC(screenDc);
            _brightDc = PInvoke.CreateCompatibleDC(screenDc);
            _backDc = PInvoke.CreateCompatibleDC(screenDc);
            _backBmp = PInvoke.CreateCompatibleBitmap(screenDc, _bounds.Width, _bounds.Height);
            PInvoke.SelectObject(_backDc, _backBmp);

            _brightBmp = ScreenCapture.CreateDib(_bounds.Width, _bounds.Height, out var bright);
            _brightBits = bright;
            _dimBmp = ScreenCapture.CreateDib(_bounds.Width, _bounds.Height, out var dim);
            PInvoke.SelectObject(_brightDc, _brightBmp);
            PInvoke.SelectObject(_dimDc, _dimBmp);

            int srcX = _bounds.X - frozen.ScreenX, srcY = _bounds.Y - frozen.ScreenY;
            int rowBytes = _bounds.Width * 4;
            fixed (byte* src = frozen.Pixels)
            {
                for (int y = 0; y < _bounds.Height; y++)
                {
                    byte* s = src + ((long)(srcY + y) * frozen.Width + srcX) * 4;
                    byte* b = (byte*)bright + (long)y * rowBytes;
                    byte* d = (byte*)dim + (long)y * rowBytes;
                    Buffer.MemoryCopy(s, b, rowBytes, rowBytes);
                    for (int x = 0; x < rowBytes; x += 4)
                    {
                        d[x] = (byte)(s[x] * 100 >> 8);         // ~39% brightness
                        d[x + 1] = (byte)(s[x + 1] * 100 >> 8);
                        d[x + 2] = (byte)(s[x + 2] * 100 >> 8);
                        d[x + 3] = 0xFF;
                    }
                }
            }
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
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
                PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS));
                return (LRESULT)1;
            case PInvoke.WM_MOUSEMOVE:
                _owner.OnMouseMove(CursorPos());
                return (LRESULT)0;
            case PInvoke.WM_LBUTTONDOWN:
                PInvoke.SetCapture(hwnd);
                _owner.OnMouseDown(CursorPos());
                return (LRESULT)0;
            case PInvoke.WM_LBUTTONUP:
                PInvoke.ReleaseCapture();
                _owner.OnMouseUp(CursorPos());
                return (LRESULT)0;
            case PInvoke.WM_RBUTTONDOWN:
                _owner.Cancel();
                return (LRESULT)0;
            case PInvoke.WM_KEYDOWN:
                if (wParam.Value == (nuint)VIRTUAL_KEY.VK_ESCAPE) _owner.Cancel();
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_RETURN) _owner.Confirm();
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_Z) _owner.ToggleLens();
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_C) _owner.CopyColor();
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_LEFT || wParam.Value == (nuint)VIRTUAL_KEY.VK_A) _owner.Nudge(-1, 0);
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_RIGHT || wParam.Value == (nuint)VIRTUAL_KEY.VK_D) _owner.Nudge(1, 0);
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_UP || wParam.Value == (nuint)VIRTUAL_KEY.VK_W) _owner.Nudge(0, -1);
                else if (wParam.Value == (nuint)VIRTUAL_KEY.VK_DOWN || wParam.Value == (nuint)VIRTUAL_KEY.VK_S) _owner.Nudge(0, 1);
                return (LRESULT)0;
            case PInvoke.WM_TIMER:
                if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_ESCAPE) & 0x8000) != 0) _owner.Cancel();
                return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private static Point CursorPos()
    {
        PInvoke.GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    private void Paint()
    {
        var hdc = PInvoke.BeginPaint(Handle, out var ps);
        try
        {
            int w = _bounds.Width, h = _bounds.Height;
            PInvoke.BitBlt(_backDc, 0, 0, w, h, _dimDc, 0, 0, ROP_CODE.SRCCOPY);

            var sel = _owner.Selection;
            var local = Rectangle.Empty;
            if (sel is { } s)
            {
                var clipped = Rectangle.Intersect(s, _bounds);
                if (!clipped.IsEmpty)
                {
                    local = new Rectangle(clipped.X - _bounds.X, clipped.Y - _bounds.Y, clipped.Width, clipped.Height);
                    PInvoke.BitBlt(_backDc, local.X, local.Y, local.Width, local.Height, _brightDc, local.X, local.Y, ROP_CODE.SRCCOPY);
                }
                DrawBorder(s);
            }

            DrawCrosshair(_owner.Cursor);
            if (_owner.LensEnabled) DrawLens(_owner.Cursor);
            else if (sel is { } s2 && _owner.ShowSize) DrawLabel(s2, _owner.Cursor);

            PInvoke.BitBlt(hdc, 0, 0, w, h, _backDc, 0, 0, ROP_CODE.SRCCOPY);
        }
        finally
        {
            PInvoke.EndPaint(Handle, ps);
        }
    }

    private void DrawBorder(Rectangle virtualSel)
    {
        int bw = Math.Max(2, (int)Math.Round(2 * _scale));
        var pen = PInvoke.CreatePen(PEN_STYLE.PS_INSIDEFRAME, bw, new COLORREF(0x00D47800)); // #0078D4
        var oldPen = PInvoke.SelectObject(_backDc, pen);
        var oldBrush = PInvoke.SelectObject(_backDc, PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.NULL_BRUSH));
        // Draw the frame just outside the selection so it never covers captured pixels.
        var r = virtualSel;
        r.Inflate(bw, bw);
        PInvoke.Rectangle(_backDc, r.Left - _bounds.X, r.Top - _bounds.Y, r.Right - _bounds.X, r.Bottom - _bounds.Y);
        PInvoke.SelectObject(_backDc, oldBrush);
        PInvoke.SelectObject(_backDc, oldPen);
        PInvoke.DeleteObject(pen);
    }

    private void DrawCrosshair(Point cursor)
    {
        int cx = cursor.X - _bounds.X, cy = cursor.Y - _bounds.Y;
        var pen = PInvoke.CreatePen(PEN_STYLE.PS_SOLID, 1, new COLORREF(0x00E0E0E0));
        var oldPen = PInvoke.SelectObject(_backDc, pen);
        if (cx >= 0 && cx < _bounds.Width)
        {
            PInvoke.MoveToEx(_backDc, cx, 0, null);
            PInvoke.LineTo(_backDc, cx, _bounds.Height);
        }
        if (cy >= 0 && cy < _bounds.Height)
        {
            PInvoke.MoveToEx(_backDc, 0, cy, null);
            PInvoke.LineTo(_backDc, _bounds.Width, cy);
        }
        PInvoke.SelectObject(_backDc, oldPen);
        PInvoke.DeleteObject(pen);
    }

    private void DrawString(string text, ref RECT rect, DRAW_TEXT_FORMAT format)
    {
        fixed (char* p = text)
            PInvoke.DrawText(_backDc, p, text.Length, ref rect, format);
    }

    private void DrawLabel(Rectangle sel, Point cursor)
    {
        // Only the monitor under the cursor shows the label.
        if (!_bounds.Contains(cursor)) return;

        string text = $"{sel.Width} × {sel.Height}";
        var (bw, bh) = MeasurePill(text);
        int gap = (int)(18 * _scale);
        int x = cursor.X - _bounds.X + gap, y = cursor.Y - _bounds.Y + gap;
        if (x + bw > _bounds.Width) x = cursor.X - _bounds.X - gap - bw;
        if (y + bh > _bounds.Height) y = cursor.Y - _bounds.Y - gap - bh;
        DrawPill(text, x, y, bw, bh);
    }

    private (int Width, int Height) MeasurePill(string text)
    {
        var oldFont = PInvoke.SelectObject(_backDc, _font);
        var measure = new RECT();
        DrawString(text, ref measure, DRAW_TEXT_FORMAT.DT_CALCRECT | DRAW_TEXT_FORMAT.DT_SINGLELINE);
        PInvoke.SelectObject(_backDc, oldFont);
        return (measure.right + (int)(8 * _scale) * 2, measure.bottom + (int)(4 * _scale) * 2);
    }

    /// <summary>Dark rounded pill with white text; <paramref name="bw"/>/<paramref name="bh"/> come from <see cref="MeasurePill"/>.</summary>
    private void DrawPill(string text, int x, int y, int bw, int bh)
    {
        int padX = (int)(8 * _scale), padY = (int)(4 * _scale);
        var oldFont = PInvoke.SelectObject(_backDc, _font);
        PInvoke.SetBkMode(_backDc, BACKGROUND_MODE.TRANSPARENT);
        PInvoke.SetTextColor(_backDc, new COLORREF(0x00FFFFFF));

        var brush = PInvoke.CreateSolidBrush(new COLORREF(0x00282828));
        var nullPen = PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.NULL_PEN);
        var oldBrush = PInvoke.SelectObject(_backDc, brush);
        var oldPen = PInvoke.SelectObject(_backDc, nullPen);
        int radius = (int)(8 * _scale);
        PInvoke.RoundRect(_backDc, x, y, x + bw, y + bh, radius, radius);
        PInvoke.SelectObject(_backDc, oldPen);
        PInvoke.SelectObject(_backDc, oldBrush);
        PInvoke.DeleteObject(brush);

        var textRect = new RECT { left = x + padX, top = y + padY, right = x + bw - padX, bottom = y + bh - padY };
        DrawString(text, ref textRect, DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX);
        PInvoke.SelectObject(_backDc, oldFont);
    }

    /// <summary>
    /// Magnifier for pixel-precise aiming: a grid of source pixels around the cursor scaled up without
    /// smoothing, the cursor's pixel framed in the middle, and a readout of position and color.
    /// </summary>
    private void DrawLens(Point cursor)
    {
        int cx = cursor.X - _bounds.X, cy = cursor.Y - _bounds.Y;
        int w = _bounds.Width, h = _bounds.Height;
        if (cx < 0 || cy < 0 || cx >= w || cy >= h) return; // only the monitor under the cursor

        const int pixels = 17; // odd, so the cursor pixel sits exactly in the middle
        int cell = Math.Max(6, (int)Math.Round(8 * _scale));
        int size = pixels * cell, half = pixels / 2;
        int gap = (int)(24 * _scale), captionRoom = (int)(34 * _scale);

        int lx = cx + gap, ly = cy + gap;
        if (lx + size > w) lx = cx - gap - size;
        if (ly + size + captionRoom > h) ly = cy - gap - size - captionRoom;
        lx = Math.Max(0, lx);
        ly = Math.Max(0, ly);

        // Black backdrop covers the part of the lens that hangs over the monitor edge.
        var black = PInvoke.CreateSolidBrush(new COLORREF(0));
        var backdrop = new RECT { left = lx, top = ly, right = lx + size, bottom = ly + size };
        PInvoke.FillRect(_backDc, backdrop, black);
        PInvoke.DeleteObject(black);

        int sx = cx - half, sy = cy - half;
        int ix0 = Math.Max(sx, 0), iy0 = Math.Max(sy, 0), ix1 = Math.Min(sx + pixels, w), iy1 = Math.Min(sy + pixels, h);
        if (ix1 > ix0 && iy1 > iy0)
        {
            PInvoke.SetStretchBltMode(_backDc, STRETCH_BLT_MODE.COLORONCOLOR); // nearest neighbour: crisp pixels
            PInvoke.StretchBlt(_backDc, lx + (ix0 - sx) * cell, ly + (iy0 - sy) * cell, (ix1 - ix0) * cell, (iy1 - iy0) * cell,
                _brightDc, ix0, iy0, ix1 - ix0, iy1 - iy0, ROP_CODE.SRCCOPY);
        }

        var nullBrush = PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.NULL_BRUSH);
        var oldBrush = PInvoke.SelectObject(_backDc, nullBrush);
        // Frame around the cursor pixel: white outside, black inside, visible on any colour.
        FrameRect(lx + half * cell - 1, ly + half * cell - 1, cell + 2, 2, 0x00FFFFFF);
        FrameRect(lx + half * cell + 1, ly + half * cell + 1, cell - 2, 1, 0x00000000);
        // Lens border.
        FrameRect(lx - 2, ly - 2, size + 4, 2, 0x00282828);
        FrameRect(lx, ly, size, 1, 0x00FFFFFF);
        PInvoke.SelectObject(_backDc, oldBrush);

        // Readout: screen position, colour under the cursor, and the selection size while dragging.
        byte* px = (byte*)_brightBits + ((long)cy * w + cx) * 4;
        string text = $"{cursor.X}, {cursor.Y}   #{px[2]:X2}{px[1]:X2}{px[0]:X2}";
        if (_owner.ColorCopied) text += " (copied)";
        if (_owner.Selection is { Width: > 0, Height: > 0 } sel) text += $"   {sel.Width} × {sel.Height}";
        var (bw, bh) = MeasurePill(text);
        int tx = Math.Clamp(lx, 0, Math.Max(0, w - bw));
        DrawPill(text, tx, ly + size + (int)(6 * _scale), bw, bh);
    }

    /// <summary>Outline rectangle with the given stroke thickness drawn inside (x, y, side-length box).</summary>
    private void FrameRect(int x, int y, int side, int thickness, uint colorRef)
    {
        var pen = PInvoke.CreatePen(PEN_STYLE.PS_INSIDEFRAME, thickness, new COLORREF(colorRef));
        var oldPen = PInvoke.SelectObject(_backDc, pen);
        PInvoke.Rectangle(_backDc, x, y, x + side, y + side);
        PInvoke.SelectObject(_backDc, oldPen);
        PInvoke.DeleteObject(pen);
    }
    public void Dispose()
    {
        if (Handle != HWND.Null)
        {
            Windows.Remove(Handle);
            PInvoke.KillTimer(Handle, EscTimerId);
            PInvoke.DestroyWindow(Handle);
            Handle = HWND.Null;
        }
        foreach (var dc in new[] { _dimDc, _brightDc, _backDc }) if (dc != HDC.Null) PInvoke.DeleteDC(dc);
        foreach (var bmp in new[] { _dimBmp, _brightBmp, _backBmp }) if (bmp != HBITMAP.Null) PInvoke.DeleteObject(bmp);
        if (_font != HFONT.Null) PInvoke.DeleteObject(_font);
        _dimDc = _brightDc = _backDc = HDC.Null;
        _dimBmp = _brightBmp = _backBmp = HBITMAP.Null;
        _font = HFONT.Null;
    }
}

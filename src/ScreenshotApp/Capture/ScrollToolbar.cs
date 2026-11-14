using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Capture;

/// <summary>
/// The small dark bar shown next to the area during a scrolling capture: progress text and a Stop button.
/// A topmost Win32 window that does not take focus and is excluded from screen capture.
/// </summary>
internal sealed unsafe class ScrollToolbar : IDisposable
{
    private const string ClassName = "ScreenshotApp.ScrollToolbar";
    private const int Width = 300, Height = 40, Pad = 4, ButtonWidth = 64, Radius = 8, Gap = 6; // device-independent pixels
    private const uint Background = 0x002B2B2B, Highlight = 0x00454545, Text = 0x00FFFFFF, Dim = 0x00C8C8C8;

    private static readonly Dictionary<nint, ScrollToolbar> Windows = new();
    private static bool s_classRegistered;

    private HWND _hwnd;
    private HFONT _font;
    private double _scale = 1;
    private Rectangle _bounds;
    private string _text = "Scrolling capture…";
    private bool _hot, _pressed;

    /// <summary>Stop was clicked.</summary>
    public event Action? StopRequested;

    public ScrollToolbar(Rectangle area)
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
        PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
        Place(area);
    }

    /// <summary>Below the area, else above it, else inside at its bottom; centred, on the area's monitor.</summary>
    private void Place(Rectangle area)
    {
        var mon = Sharing.ShareToolbar.MonitorOf(area);
        // Land on that monitor first so the DPI (and so the size) is the right one.
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), mon.X + mon.Width / 2, mon.Y + mon.Height / 2, 1, 1, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _scale = PInvoke.GetDpiForWindow(_hwnd) / 96.0;
        _font = PInvoke.CreateFont(-Px(13), 0, 0, 0, 400, 0, 0, 0, FONT_CHARSET.DEFAULT_CHARSET, 0, 0, FONT_QUALITY.CLEARTYPE_QUALITY, 0, "Segoe UI");

        int w = Px(Width), h = Px(Height), gap = Px(Gap);
        int x = Math.Clamp(area.X + area.Width / 2 - w / 2, mon.Left, Math.Max(mon.Left, mon.Right - w));
        var below = new Rectangle(x, area.Bottom + gap, w, h);
        var above = new Rectangle(x, area.Top - gap - h, w, h);
        var placed = mon.Contains(below) ? below
            : mon.Contains(above) ? above
            : new Rectangle(x, Math.Max(mon.Top, Math.Min(area.Bottom, mon.Bottom) - gap - h), w, h);
        _bounds = placed;
        PInvoke.SetWindowPos(_hwnd, new HWND(-1), placed.X, placed.Y, w, h,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        int r = Px(Radius);
        PInvoke.SetWindowRgn(_hwnd, PInvoke.CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2), true);
    }

    public bool Contains(Point screen) => _bounds.Contains(screen);

    public void SetText(string text)
    {
        if (text == _text || _hwnd == HWND.Null) return;
        _text = text;
        PInvoke.InvalidateRect(_hwnd, (RECT*)null, false);
    }

    private int Px(double v) => (int)Math.Round(v * _scale);

    private Rectangle ButtonRect() =>
        new(_bounds.Width - Px(Pad) - Px(ButtonWidth), Px(Pad), Px(ButtonWidth), _bounds.Height - 2 * Px(Pad));

    private bool OverButton()
    {
        PInvoke.GetCursorPos(out var pt);
        return ButtonRect().Contains(pt.X - _bounds.X, pt.Y - _bounds.Y);
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
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
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
            case PInvoke.WM_MOUSEMOVE:
            {
                bool hot = OverButton();
                if (hot != _hot)
                {
                    _hot = hot;
                    PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                }
                return (LRESULT)0;
            }
            case PInvoke.WM_LBUTTONDOWN:
                if (OverButton())
                {
                    _pressed = true;
                    PInvoke.SetCapture(hwnd);
                    PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                }
                return (LRESULT)0;
            case PInvoke.WM_LBUTTONUP:
                if (_pressed)
                {
                    _pressed = false;
                    PInvoke.ReleaseCapture();
                    PInvoke.InvalidateRect(hwnd, (RECT*)null, false);
                    if (OverButton()) StopRequested?.Invoke();
                }
                return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Paint()
    {
        var hdc = PInvoke.BeginPaint(_hwnd, out var ps);
        try
        {
            int w = _bounds.Width, h = _bounds.Height;
            var mem = PInvoke.CreateCompatibleDC(hdc);
            var bmp = PInvoke.CreateCompatibleBitmap(hdc, w, h);
            var oldBmp = PInvoke.SelectObject(mem, bmp);
            var oldFont = PInvoke.SelectObject(mem, _font);
            PInvoke.SetBkMode(mem, BACKGROUND_MODE.TRANSPARENT);

            Fill(mem, new RECT { right = w, bottom = h }, Background);
            var b = ButtonRect();
            Fill(mem, new RECT { left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom },
                _hot || _pressed ? 0x002311E8u : Highlight);

            PInvoke.SetTextColor(mem, new COLORREF(Dim));
            var textRect = new RECT { left = Px(12), top = 0, right = b.Left - Px(8), bottom = h };
            DrawString(mem, _text, ref textRect, DRAW_TEXT_FORMAT.DT_LEFT | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_END_ELLIPSIS);
            PInvoke.SetTextColor(mem, new COLORREF(Text));
            var buttonRect = new RECT { left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom };
            DrawString(mem, "Stop", ref buttonRect, DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE);

            PInvoke.BitBlt(hdc, 0, 0, w, h, mem, 0, 0, ROP_CODE.SRCCOPY);
            PInvoke.SelectObject(mem, oldFont);
            PInvoke.SelectObject(mem, oldBmp);
            PInvoke.DeleteObject(bmp);
            PInvoke.DeleteDC(mem);
        }
        finally
        {
            PInvoke.EndPaint(_hwnd, ps);
        }
    }

    private static void DrawString(HDC hdc, string text, ref RECT rect, DRAW_TEXT_FORMAT format)
    {
        fixed (char* p = text)
            PInvoke.DrawText(hdc, p, text.Length, ref rect, format);
    }

    private static void Fill(HDC hdc, RECT rect, uint color)
    {
        var brush = PInvoke.CreateSolidBrush(new COLORREF(color));
        PInvoke.FillRect(hdc, rect, brush);
        PInvoke.DeleteObject(brush);
    }

    public void Dispose()
    {
        if (_hwnd == HWND.Null) return;
        Windows.Remove(_hwnd);
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = HWND.Null;
        if (_font != HFONT.Null)
        {
            PInvoke.DeleteObject(_font);
            _font = HFONT.Null;
        }
    }
}

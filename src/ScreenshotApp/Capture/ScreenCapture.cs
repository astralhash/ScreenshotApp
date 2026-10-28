using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ScreenshotApp.Capture;

/// <summary>GDI screen grabbing. All coordinates are physical pixels (process is per-monitor-v2 DPI aware).</summary>
internal static unsafe class ScreenCapture
{
    /// <summary>Bounds of the monitor containing the cursor.</summary>
    public static Rectangle GetMonitorUnderCursor()
    {
        PInvoke.GetCursorPos(out var pt);
        var monitor = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("GetMonitorInfo failed.");
        var r = info.rcMonitor;
        return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
    }

    /// <summary>Creates a top-down 32bpp DIB section; <paramref name="bits"/> points at its pixels.</summary>
    public static HBITMAP CreateDib(int width, int height, out void* bits)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = -height;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        var dc = PInvoke.GetDC(HWND.Null);
        try
        {
            void* p;
            var dib = PInvoke.CreateDIBSection(dc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &p, HANDLE.Null, 0);
            if (dib.IsNull) throw new InvalidOperationException("CreateDIBSection failed.");
            bits = p;
            return dib;
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, dc);
        }
    }

    public static CapturedImage CaptureMonitorUnderCursor() => CaptureRect(GetMonitorUnderCursor());

    public static CapturedImage CaptureRect(Rectangle area)
    {
        int w = area.Width, h = area.Height;
        var screenDc = PInvoke.GetDC(HWND.Null);
        var memDc = PInvoke.CreateCompatibleDC(screenDc);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            void* bits;
            var dib = PInvoke.CreateDIBSection(screenDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
            if (dib.IsNull)
                throw new InvalidOperationException("CreateDIBSection failed.");

            var old = PInvoke.SelectObject(memDc, dib);
            try
            {
                // CAPTUREBLT includes layered (topmost translucent) windows.
                if (!PInvoke.BitBlt(memDc, 0, 0, w, h, screenDc, area.X, area.Y,
                        ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
                    throw new InvalidOperationException("BitBlt failed.");

                var pixels = new byte[w * h * 4];
                Marshal.Copy((IntPtr)bits, pixels, 0, pixels.Length);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF; // GDI leaves alpha undefined
                return new CapturedImage(pixels, w, h, area.X, area.Y);
            }
            finally
            {
                PInvoke.SelectObject(memDc, old);
                PInvoke.DeleteObject(dib);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memDc);
            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }
}

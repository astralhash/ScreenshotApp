using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Graphics.Canvas;
using ScreenshotApp.Services;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ScreenshotApp.Capture;

/// <summary>
/// Grabs a single window with Windows.Graphics.Capture: windows in front of it are not in the shot and the
/// z-order is left alone. Returns null whenever that is not possible, so the caller can use the frozen screen.
/// </summary>
internal static class WindowCapture
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Captures <paramref name="hwnd"/> into the area of <paramref name="frozen"/> (its crop of the frozen screen).
    /// Pixels the window leaves transparent (rounded corners, Mica backdrop) are filled from the frozen screen,
    /// or with a plain backdrop where <paramref name="occluders"/> covered the window.
    /// </summary>
    public static async Task<CapturedImage?> TryCaptureAsync(HWND hwnd, CapturedImage frozen, IReadOnlyList<Rectangle> occluders)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return null;
        try
        {
            if (await GrabAsync(hwnd) is not { } frame) return null;
            return Compose(frame.Pixels, frame.Width, frame.Height, FrameOrigin(hwnd, frame.Width, frame.Height), frozen, occluders);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    internal static async Task<(byte[] Pixels, int Width, int Height)?> GrabAsync(HWND hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported()) return null;
        var item = CaptureInterop.CreateForWindow(hwnd);
        var device = CanvasDevice.GetSharedDevice();
        var tcs = new TaskCompletionSource<(byte[], int, int)?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        pool.FrameArrived += (p, _) =>
        {
            try
            {
                using var frame = p.TryGetNextFrame();
                if (frame is null) return;
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, frame.Surface);
                // The surface keeps the pool's size; the window's content may be smaller if it was resizing.
                int w = Math.Min(frame.ContentSize.Width, (int)bitmap.SizeInPixels.Width);
                int h = Math.Min(frame.ContentSize.Height, (int)bitmap.SizeInPixels.Height);
                tcs.TrySetResult((bitmap.GetPixelBytes(0, 0, w, h), w, h));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };

        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        CaptureInterop.TryDisableBorder(session);
        session.StartCapture();

        var done = await Task.WhenAny(tcs.Task, Task.Delay(FrameTimeout));
        return done == tcs.Task ? await tcs.Task : null;
    }

    /// <summary>
    /// Screen position of the frame's top-left pixel. Depending on the window the frame covers its whole window
    /// rect (including the invisible resize border) or just its visible frame; the frame size tells which.
    /// </summary>
    internal static Point FrameOrigin(HWND hwnd, int width, int height)
    {
        PInvoke.GetWindowRect(hwnd, out var wr);
        if (wr.right - wr.left == width && wr.bottom - wr.top == height) return new Point(wr.left, wr.top);
        return WindowEnumerator.GetFrameBounds(hwnd) is { } fb ? fb.Location : new Point(wr.left, wr.top);
    }

    private static CapturedImage Compose(byte[] src, int srcW, int srcH, Point srcOrigin,
        CapturedImage frozen, IReadOnlyList<Rectangle> occluders)
    {
        int w = frozen.Width, h = frozen.Height;
        var dst = (byte[])frozen.Pixels.Clone();
        var backdrop = BackdropColor(src);
        int offX = frozen.ScreenX - srcOrigin.X, offY = frozen.ScreenY - srcOrigin.Y;

        // Whole pixels at a time, over the part of each row the two images share: nearly every pixel is opaque
        // and is one 32-bit copy.
        var srcPixels = MemoryMarshal.Cast<byte, uint>(src.AsSpan());
        var dstPixels = MemoryMarshal.Cast<byte, uint>(dst.AsSpan());
        int x0 = Math.Max(0, -offX), x1 = Math.Min(w, srcW - offX);
        int y0 = Math.Max(0, -offY), y1 = Math.Min(h, srcH - offY);
        for (int y = y0; y < y1 && x1 > x0; y++)
        {
            var srcRow = srcPixels.Slice((y + offY) * srcW + x0 + offX, x1 - x0);
            var dstRow = dstPixels.Slice(y * w + x0, x1 - x0);
            for (int i = 0; i < srcRow.Length; i++)
            {
                uint pixel = srcRow[i], a = pixel >> 24;
                if (a == 255)
                {
                    dstRow[i] = pixel;
                    continue;
                }
                // Premultiplied "over": what the window lets through comes from the screen behind it, unless a
                // window in front covered that spot in the frozen screen.
                bool covered = IsCovered(occluders, frozen.ScreenX + x0 + i, frozen.ScreenY + y);
                uint behindPixel = dstRow[i], result = 0xFF000000;
                for (int c = 0; c < 3; c++)
                {
                    int shift = c * 8;
                    uint behind = covered ? backdrop[c] : (behindPixel >> shift) & 0xFF;
                    result |= Math.Min(255u, ((pixel >> shift) & 0xFF) + behind * (255 - a) / 255) << shift;
                }
                dstRow[i] = result;
            }
        }
        return new CapturedImage(dst, w, h, frozen.ScreenX, frozen.ScreenY);
    }

    private static bool IsCovered(IReadOnlyList<Rectangle> occluders, int x, int y)
    {
        foreach (var r in occluders)
            if (r.Contains(x, y)) return true;
        return false;
    }

    /// <summary>Light or dark Windows 11 window background (BGR), matching the window's own brightness.</summary>
    private static byte[] BackdropColor(byte[] src)
    {
        long sum = 0, count = 0;
        for (int i = 0; i < src.Length; i += 4 * 16)
        {
            if (src[i + 3] != 255) continue;
            sum += (src[i] * 114 + src[i + 1] * 587 + src[i + 2] * 299) / 1000;
            count++;
        }
        return count > 0 && sum / count < 128 ? [0x20, 0x20, 0x20] : [0xF3, 0xF3, 0xF3];
    }
}

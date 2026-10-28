using System.Runtime.InteropServices;
using ScreenshotApp.Capture;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;

namespace ScreenshotApp.Services;

/// <summary>
/// Copies an image with plain Win32 clipboard calls (CF_DIB plus a registered "PNG" format), so it works
/// even when none of our windows has focus (e.g. a capture that only targets the clipboard).
/// </summary>
internal static class ClipboardService
{
    private const uint CF_DIB = 8;

    /// <summary>The clipboard owner; set once at startup to the hidden message window.</summary>
    public static HWND Owner { get; set; }

    /// <param name="png">The image already compressed as PNG, if the caller has it; otherwise it is encoded here.</param>
    public static async Task CopyImageAsync(CapturedImage image, byte[]? png = null)
    {
        png ??= await ImageSaver.EncodeAsync(image, jpg: false);
        SetClipboard(image, png);
    }

    private static unsafe void SetClipboard(CapturedImage image, byte[] png)
    {
        // Clipboard managers (Ditto etc.) briefly hold the clipboard open, so retry.
        bool opened = false;
        for (int i = 0; i < 20 && !(opened = PInvoke.OpenClipboard(Owner)); i++)
            Thread.Sleep(25);
        if (!opened) throw new IOException("The clipboard is busy.");

        try
        {
            PInvoke.EmptyClipboard();
            PutGlobal(CF_DIB, BuildDib(image));
            fixed (char* name = "PNG")
                PutGlobal(PInvoke.RegisterClipboardFormat(name), png);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>BITMAPINFOHEADER followed by bottom-up BGRA rows (the most widely accepted CF_DIB layout).</summary>
    private static unsafe byte[] BuildDib(CapturedImage image)
    {
        int header = sizeof(BITMAPINFOHEADER), stride = image.Width * 4;
        var dib = new byte[header + stride * image.Height];
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)header,
            biWidth = image.Width,
            biHeight = image.Height, // positive = bottom-up
            biPlanes = 1,
            biBitCount = 32,
            biSizeImage = (uint)(stride * image.Height),
        };
        fixed (byte* d = dib) *(BITMAPINFOHEADER*)d = info;

        for (int y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Pixels, y * stride, dib, header + (image.Height - 1 - y) * stride, stride);
        return dib;
    }

    private static unsafe void PutGlobal(uint format, byte[] data)
    {
        var handle = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (handle == HGLOBAL.Null) throw new OutOfMemoryException("Could not allocate clipboard memory.");

        var target = PInvoke.GlobalLock(handle);
        Marshal.Copy(data, 0, (IntPtr)target, data.Length);
        PInvoke.GlobalUnlock(handle);

        // On success the clipboard owns the memory; on failure we must free it.
        if (PInvoke.SetClipboardData(format, (HANDLE)handle.Value).IsNull)
        {
            PInvoke.GlobalFree(handle);
            throw new IOException("SetClipboardData failed.");
        }
    }
}

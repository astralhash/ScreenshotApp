using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor.Model;
using Windows.Graphics.DirectX;

namespace ScreenshotApp.Editor;

/// <summary>Flattens the base image and annotations at full pixel resolution with Win2D, applying the crop.</summary>
internal static class ImageExporter
{
    public static CapturedImage Render(CapturedImage source, IEnumerable<Annotation> annotations, RectangleF? crop = null)
    {
        using var target = Flatten(source, annotations, crop, out int width, out int height);
        return new CapturedImage(target.GetPixelBytes(), width, height, source.ScreenX, source.ScreenY);
    }

    /// <summary>
    /// The same output as <see cref="Render"/>, scaled down (never up) to fit <paramref name="maxWidth"/> x
    /// <paramref name="maxHeight"/>. The downscale happens on the GPU, so only the small result is read back.
    /// </summary>
    public static CapturedImage RenderScaled(CapturedImage source, IEnumerable<Annotation> annotations, RectangleF? crop,
        int maxWidth, int maxHeight)
    {
        using var full = Flatten(source, annotations, crop, out int width, out int height);
        double k = Math.Min(Math.Min((double)maxWidth / width, (double)maxHeight / height), 1.0);
        int w = Math.Max(1, (int)(width * k)), h = Math.Max(1, (int)(height * k));

        using var small = new CanvasRenderTarget(
            full.Device, w, h, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = small.CreateDrawingSession())
        {
            ds.DrawImage(full, new Windows.Foundation.Rect(0, 0, w, h), new Windows.Foundation.Rect(0, 0, width, height),
                1f, CanvasImageInterpolation.HighQualityCubic);
        }
        return new CapturedImage(small.GetPixelBytes(), w, h, source.ScreenX, source.ScreenY);
    }

    private static CanvasRenderTarget Flatten(CapturedImage source, IEnumerable<Annotation> annotations, RectangleF? crop,
        out int width, out int height)
    {
        var area = crop ?? new RectangleF(0, 0, source.Width, source.Height);
        width = Math.Max(1, (int)area.Width);
        height = Math.Max(1, (int)area.Height);

        var device = CanvasDevice.GetSharedDevice();
        using var bitmap = CanvasBitmap.CreateFromBytes(
            device, source.Pixels, source.Width, source.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        // 96 dpi: one image pixel is one DIP, so nothing is scaled by the display.
        var target = new CanvasRenderTarget(
            device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        try
        {
            using var ds = target.CreateDrawingSession();
            // Shifting by the crop origin makes everything outside the crop fall off the target.
            ds.Transform = Matrix3x2.CreateTranslation(-area.X, -area.Y);
            ds.DrawImage(bitmap);
            var ctx = new RenderContext(bitmap);
            foreach (var a in annotations) a.Render(ds, ctx);
        }
        catch
        {
            target.Dispose();
            throw;
        }
        return target;
    }
}

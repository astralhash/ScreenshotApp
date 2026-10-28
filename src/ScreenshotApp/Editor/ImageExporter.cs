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
        var area = crop ?? new RectangleF(0, 0, source.Width, source.Height);
        int width = Math.Max(1, (int)area.Width), height = Math.Max(1, (int)area.Height);

        var device = CanvasDevice.GetSharedDevice();
        using var bitmap = CanvasBitmap.CreateFromBytes(
            device, source.Pixels, source.Width, source.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
        // 96 dpi: one image pixel is one DIP, so nothing is scaled by the display.
        using var target = new CanvasRenderTarget(
            device, width, height, 96, DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using (var ds = target.CreateDrawingSession())
        {
            // Shifting by the crop origin makes everything outside the crop fall off the target.
            ds.Transform = Matrix3x2.CreateTranslation(-area.X, -area.Y);
            ds.DrawImage(bitmap);
            var ctx = new RenderContext(bitmap);
            foreach (var a in annotations) a.Render(ds, ctx);
        }
        return new CapturedImage(target.GetPixelBytes(), width, height, source.ScreenX, source.ScreenY);
    }
}

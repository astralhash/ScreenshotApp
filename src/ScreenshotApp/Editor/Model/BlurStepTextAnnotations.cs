using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Color = Windows.UI.Color;

namespace ScreenshotApp.Editor.Model;

/// <summary>
/// How a redaction hides what is under it. <see cref="Solid"/> is the only one that is irreversible by construction
/// (the output no longer depends on the original pixels); the others only reduce the information.
/// </summary>
internal enum BlurMode { Pixelate, Blur, Solid }

/// <summary>
/// Hides the part of the base image under a rectangle (it samples the base image, so it hides the capture, not
/// other annotations). Hardened against recovery:
/// <list type="bullet">
/// <item>Solid fills with an opaque colour, so nothing of the original survives.</item>
/// <item>Pixelate and Blur have a minimum strength regardless of the slider, and their result is quantized to a few
/// brightness levels per channel. Deblurring and mosaic-matching attacks rely on the small intensity differences
/// that quantization throws away.</item>
/// <item>The area is snapped outwards to whole pixels, so an edge that falls between pixels can never leave a thin
/// strip of the original showing.</item>
/// </list>
/// </summary>
internal sealed class BlurAnnotation : BoxAnnotation
{
    private const float MinPixelBlock = 10f, MinBlurRadius = 8f;
    private const int PixelateLevels = 8, BlurLevels = 16;

    public BlurMode Mode { get; set; } = BlurMode.Pixelate;

    public override bool SupportsFill => false;

    /// <summary>The box rounded outwards to whole pixels. This is the exact area that gets redacted.</summary>
    public RectangleF SnappedBox
    {
        get
        {
            var b = Box;
            return RectangleF.FromLTRB(MathF.Floor(b.Left), MathF.Floor(b.Top), MathF.Ceiling(b.Right), MathF.Ceiling(b.Bottom));
        }
    }

    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        var r = SnappedBox;
        if (r.Width < 1 || r.Height < 1) return;
        var area = new Rect(r.X, r.Y, r.Width, r.Height);

        if (Mode == BlurMode.Solid)
        {
            ds.FillRectangle(area, Color.FromArgb(255, Color.R, Color.G, Color.B));
            return;
        }

        using var clip = ds.CreateLayer(1f, area);
        if (Mode == BlurMode.Pixelate)
        {
            float block = Math.Max(MinPixelBlock, StrokeWidth * 2f);
            // Average down, then scale back up without smoothing: a mosaic aligned to the image grid.
            using var down = new ScaleEffect
            {
                Source = ctx.BaseImage,
                Scale = new Vector2(1f / block),
                InterpolationMode = CanvasImageInterpolation.HighQualityCubic,
                BorderMode = EffectBorderMode.Hard,
            };
            using var up = new ScaleEffect
            {
                Source = down,
                Scale = new Vector2(block),
                InterpolationMode = CanvasImageInterpolation.NearestNeighbor,
                BorderMode = EffectBorderMode.Hard,
            };
            using var coarse = Quantize(up, PixelateLevels);
            ds.DrawImage(coarse);
        }
        else
        {
            using var blur = new GaussianBlurEffect
            {
                Source = ctx.BaseImage,
                BlurAmount = Math.Max(MinBlurRadius, StrokeWidth * 1.5f),
                BorderMode = EffectBorderMode.Hard,
            };
            using var coarse = Quantize(blur, BlurLevels);
            ds.DrawImage(coarse);
        }
    }

    /// <summary>Reduces every colour channel to <paramref name="levels"/> evenly spaced values (alpha untouched).</summary>
    private static DiscreteTransferEffect Quantize(ICanvasImage source, int levels)
    {
        var table = new float[levels];
        for (int i = 0; i < levels; i++) table[i] = i / (float)(levels - 1);
        return new DiscreteTransferEffect
        {
            Source = source,
            RedTable = table,
            GreenTable = table,
            BlueTable = table,
            AlphaDisable = true,
        };
    }
    public override bool HitTest(Vector2 p, float tolerance)
    {
        var box = SnappedBox;
        box.Inflate(tolerance, tolerance);
        return box.Contains(p.X, p.Y);
    }

    public override Annotation Clone() => CopyBaseTo(new BlurAnnotation { A = A, B = B, Mode = Mode });
}

/// <summary>Numbered circle ("step 1", "step 2"...). Click to place; the width sets the size.</summary>
internal sealed class StepAnnotation : Annotation
{
    public Vector2 Center { get; set; }
    public int Number { get; set; } = 1;

    public float Radius => 8f + StrokeWidth * 2f;

    public override RectangleF Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override void Move(Vector2 delta) => Center += delta;

    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        float r = Radius;
        ds.FillCircle(Center, r, Color);
        ds.DrawCircle(Center, r, Color.FromArgb(200, 255, 255, 255), Math.Max(1.5f, r * 0.08f)); // keeps it readable on any background

        // Black or white digits, whichever contrasts with the fill.
        double luminance = (0.299 * Color.R + 0.587 * Color.G + 0.114 * Color.B) / 255;
        var textColor = luminance > 0.62 ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 255, 255, 255);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = r * (Number >= 10 ? 0.95f : 1.15f),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        ds.DrawText(Number.ToString(), new Rect(Center.X - r, Center.Y - r, r * 2, r * 2), textColor, format);
    }

    public override bool HitTest(Vector2 p, float tolerance) => Vector2.Distance(p, Center) <= Radius + tolerance;

    public override Annotation Clone() => CopyBaseTo(new StepAnnotation { Center = Center, Number = Number });
}

/// <summary>Text label. The width sets the font size (4x). Position is the top-left corner.</summary>
internal sealed class TextAnnotation : Annotation
{
    public Vector2 Position { get; set; }
    public string Text { get; set; } = "";

    public float FontSize => Math.Max(8f, StrokeWidth * 4f);

    public static CanvasTextFormat CreateFormat(float fontSize) => new()
    {
        FontFamily = "Segoe UI",
        FontSize = fontSize,
        WordWrapping = CanvasWordWrapping.NoWrap,
    };

    public override RectangleF Bounds
    {
        get
        {
            using var format = CreateFormat(FontSize);
            using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), Text.Length == 0 ? " " : Text, format, 100000, 100000);
            var b = layout.LayoutBounds;
            return new RectangleF(Position.X + (float)b.X, Position.Y + (float)b.Y, (float)b.Width, (float)b.Height);
        }
    }

    public override void Move(Vector2 delta) => Position += delta;

    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        if (Text.Length == 0) return;
        using var format = CreateFormat(FontSize);
        ds.DrawText(Text, Position, Color, format);
    }

    public override bool HitTest(Vector2 p, float tolerance)
    {
        var box = Bounds;
        box.Inflate(tolerance, tolerance);
        return box.Contains(p.X, p.Y);
    }

    public override Annotation Clone() => CopyBaseTo(new TextAnnotation { Position = Position, Text = Text });
}

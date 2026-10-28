using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Color = Windows.UI.Color;

namespace ScreenshotApp.Editor.Model;

internal enum CursorKind { Arrow, Cross, Move, ResizeNwSe, ResizeNeSw, ResizeNs, ResizeWe }

/// <summary>What an annotation may need while rendering (e.g. blur samples the base image).</summary>
internal sealed record RenderContext(ICanvasImage BaseImage);

/// <summary>
/// A drawable object in image-pixel coordinates. No WinUI types: only Win2D drawing and System.Numerics/Drawing.
/// <see cref="Clone"/> keeps the same <see cref="Id"/> so commands can find the object again.
/// </summary>
internal abstract class Annotation
{
    public Guid Id { get; internal set; } = Guid.NewGuid();
    public Color Color { get; set; } = Color.FromArgb(255, 229, 57, 53);
    public float StrokeWidth { get; set; } = 4;
    public bool Fill { get; set; }

    public virtual bool SupportsFill => false;

    public abstract void Render(CanvasDrawingSession ds, RenderContext ctx);

    /// <summary>True when <paramref name="p"/> (image px) is on the object; <paramref name="tolerance"/> is in image px.</summary>
    public abstract bool HitTest(Vector2 p, float tolerance);

    public abstract RectangleF Bounds { get; }
    public abstract Annotation Clone();
    public abstract void Move(Vector2 delta);

    public virtual int HandleCount => 0;
    public virtual Vector2 GetHandle(int index) => throw new NotSupportedException();
    public virtual void SetHandle(int index, Vector2 position, bool shift) { }
    public virtual CursorKind GetHandleCursor(int index) => CursorKind.Move;

    /// <summary>Whether <see cref="Bounds"/> should be outlined when selected (lines are not).</summary>
    public virtual bool ShowBoundsWhenSelected => true;

    protected T CopyBaseTo<T>(T target) where T : Annotation
    {
        target.Id = Id;
        target.Color = Color;
        target.StrokeWidth = StrokeWidth;
        target.Fill = Fill;
        return target;
    }

    protected Color FillColor => Color.FromArgb(90, Color.R, Color.G, Color.B);
}

/// <summary>Eight-handle rectangle resize shared by box shapes and scaled paths.</summary>
internal static class HandleGeometry
{
    // 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
    public static Vector2 Get(RectangleF r, int i) => i switch
    {
        0 => new(r.Left, r.Top),
        1 => new(r.Left + r.Width / 2, r.Top),
        2 => new(r.Right, r.Top),
        3 => new(r.Right, r.Top + r.Height / 2),
        4 => new(r.Right, r.Bottom),
        5 => new(r.Left + r.Width / 2, r.Bottom),
        6 => new(r.Left, r.Bottom),
        _ => new(r.Left, r.Top + r.Height / 2),
    };

    /// <summary>Moves the edges touched by handle <paramref name="i"/> to <paramref name="p"/>; never flips or collapses.</summary>
    public static RectangleF Apply(RectangleF r, int i, Vector2 p, float min = 1)
    {
        float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (i is 0 or 6 or 7) l = Math.Min(p.X, rt - min);
        if (i is 2 or 3 or 4) rt = Math.Max(p.X, l + min);
        if (i is 0 or 1 or 2) t = Math.Min(p.Y, b - min);
        if (i is 4 or 5 or 6) b = Math.Max(p.Y, t + min);
        return RectangleF.FromLTRB(l, t, rt, b);
    }

    public static CursorKind Cursor(int i) => i switch
    {
        0 or 4 => CursorKind.ResizeNwSe,
        2 or 6 => CursorKind.ResizeNeSw,
        1 or 5 => CursorKind.ResizeNs,
        _ => CursorKind.ResizeWe,
    };

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Snaps the direction anchor→p to the nearest 45° step (Shift behaviour).</summary>
    public static Vector2 SnapAngle(Vector2 anchor, Vector2 p)
    {
        var v = p - anchor;
        float len = v.Length();
        if (len < 1e-3f) return p;
        float step = MathF.PI / 4;
        float angle = MathF.Round(MathF.Atan2(v.Y, v.X) / step) * step;
        return anchor + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * len;
    }
}

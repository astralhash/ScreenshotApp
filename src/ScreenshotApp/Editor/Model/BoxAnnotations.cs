using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;

namespace ScreenshotApp.Editor.Model;

/// <summary>Annotation defined by two opposite corners, with eight resize handles.</summary>
internal abstract class BoxAnnotation : Annotation
{
    public Vector2 A { get; set; }
    public Vector2 B { get; set; }

    public RectangleF Box => RectangleF.FromLTRB(
        Math.Min(A.X, B.X), Math.Min(A.Y, B.Y), Math.Max(A.X, B.X), Math.Max(A.Y, B.Y));

    public override RectangleF Bounds => Box;
    public override bool SupportsFill => true;

    public override void Move(Vector2 delta)
    {
        A += delta;
        B += delta;
    }

    /// <summary>Sets the shape from a drag; Shift gives a square.</summary>
    public void SetDrag(Vector2 start, Vector2 current, bool shift)
    {
        A = start;
        var d = current - start;
        if (shift)
        {
            float side = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            d = new Vector2(d.X < 0 ? -side : side, d.Y < 0 ? -side : side);
        }
        B = start + d;
    }

    public override int HandleCount => 8;
    public override Vector2 GetHandle(int index) => HandleGeometry.Get(Box, index);
    public override CursorKind GetHandleCursor(int index) => HandleGeometry.Cursor(index);

    public override void SetHandle(int index, Vector2 position, bool shift)
    {
        var r = HandleGeometry.Apply(Box, index, position);
        A = new Vector2(r.Left, r.Top);
        B = new Vector2(r.Right, r.Bottom);
    }
}

internal sealed class RectangleAnnotation : BoxAnnotation
{
    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        var r = Box;
        if (Fill) ds.FillRectangle(r.X, r.Y, r.Width, r.Height, FillColor);
        using var style = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle { LineJoin = Microsoft.Graphics.Canvas.Geometry.CanvasLineJoin.Round };
        ds.DrawRectangle(r.X, r.Y, r.Width, r.Height, Color, StrokeWidth, style);
    }

    public override bool HitTest(Vector2 p, float tolerance)
    {
        float reach = tolerance + StrokeWidth / 2;
        var outer = Box;
        outer.Inflate(reach, reach);
        if (!outer.Contains(p.X, p.Y)) return false;
        if (Fill) return true;
        var inner = Box;
        inner.Inflate(-reach, -reach);
        return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p.X, p.Y);
    }

    public override Annotation Clone() => CopyBaseTo(new RectangleAnnotation { A = A, B = B });
}

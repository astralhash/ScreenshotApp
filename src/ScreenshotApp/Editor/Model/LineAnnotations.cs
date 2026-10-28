using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace ScreenshotApp.Editor.Model;

/// <summary>Straight segment with two endpoint handles.</summary>
internal class LineAnnotation : Annotation
{
    public Vector2 P0 { get; set; }
    public Vector2 P1 { get; set; }

    public override RectangleF Bounds => RectangleF.FromLTRB(
        Math.Min(P0.X, P1.X), Math.Min(P0.Y, P1.Y), Math.Max(P0.X, P1.X), Math.Max(P0.Y, P1.Y));

    public override bool ShowBoundsWhenSelected => false;

    public override void Move(Vector2 delta)
    {
        P0 += delta;
        P1 += delta;
    }

    public override int HandleCount => 2;
    public override Vector2 GetHandle(int index) => index == 0 ? P0 : P1;
    public override CursorKind GetHandleCursor(int index) => CursorKind.Move;

    public override void SetHandle(int index, Vector2 position, bool shift)
    {
        if (index == 0) P0 = shift ? HandleGeometry.SnapAngle(P1, position) : position;
        else P1 = shift ? HandleGeometry.SnapAngle(P0, position) : position;
    }

    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawLine(P0, P1, Color, StrokeWidth, style);
    }

    public override bool HitTest(Vector2 p, float tolerance) =>
        HandleGeometry.DistanceToSegment(p, P0, P1) <= tolerance + StrokeWidth / 2;

    public override Annotation Clone() => CopyBaseTo(new LineAnnotation { P0 = P0, P1 = P1 });
}

/// <summary>Line with a filled arrow head at <see cref="LineAnnotation.P1"/>.</summary>
internal sealed class ArrowAnnotation : LineAnnotation
{
    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        var dir = P1 - P0;
        float len = dir.Length();
        if (len < 0.5f) return;

        var u = dir / len;
        var n = new Vector2(-u.Y, u.X);
        float head = Math.Min(Math.Max(12f, StrokeWidth * 4f), len);
        float halfWidth = head * 0.5f;
        var baseCenter = P1 - u * head;

        using var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Flat };
        // Run the shaft slightly into the head so there is no seam.
        ds.DrawLine(P0, P1 - u * (head * 0.8f), Color, StrokeWidth, style);
        using var tri = CanvasGeometry.CreatePolygon(ds, new[] { P1, baseCenter + n * halfWidth, baseCenter - n * halfWidth });
        ds.FillGeometry(tri, Color);
    }

    public override bool HitTest(Vector2 p, float tolerance) =>
        HandleGeometry.DistanceToSegment(p, P0, P1) <= tolerance + Math.Max(StrokeWidth / 2, 3);

    public override Annotation Clone() => CopyBaseTo(new ArrowAnnotation { P0 = P0, P1 = P1 });
}

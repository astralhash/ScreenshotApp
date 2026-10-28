using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Color = Windows.UI.Color;

namespace ScreenshotApp.Editor.Model;

/// <summary>Freehand polyline drawn as a smoothed curve; resizing scales the points within the bounding box.</summary>
internal class PathAnnotation : Annotation
{
    public List<Vector2> Points { get; set; } = new();

    protected virtual Color EffectiveColor => Color;
    protected virtual float EffectiveWidth => StrokeWidth;
    protected virtual CanvasCapStyle Cap => CanvasCapStyle.Round;

    public override RectangleF Bounds
    {
        get
        {
            if (Points.Count == 0) return RectangleF.Empty;
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var p in Points)
            {
                l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
                r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
            }
            return RectangleF.FromLTRB(l, t, r, b);
        }
    }

    public override void Move(Vector2 delta)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += delta;
    }

    /// <summary>A two-point stroke (Shift pen, default highlighter) behaves like a line: endpoint handles only.</summary>
    private bool IsStraight => Points.Count == 2;

    public override bool ShowBoundsWhenSelected => !IsStraight;
    public override int HandleCount => IsStraight ? 2 : 8;
    public override Vector2 GetHandle(int index) => IsStraight ? Points[index] : HandleGeometry.Get(Bounds, index);
    public override CursorKind GetHandleCursor(int index) => IsStraight ? CursorKind.Move : HandleGeometry.Cursor(index);

    public override void SetHandle(int index, Vector2 position, bool shift)
    {
        if (IsStraight)
        {
            Points[index] = shift ? HandleGeometry.SnapAngle(Points[1 - index], position) : position;
            return;
        }

        var old = Bounds;
        var next = HandleGeometry.Apply(old, index, position);
        float sx = old.Width > 1e-3f ? next.Width / old.Width : 1;
        float sy = old.Height > 1e-3f ? next.Height / old.Height : 1;
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            Points[i] = new Vector2(next.Left + (p.X - old.Left) * sx, next.Top + (p.Y - old.Top) * sy);
        }
    }

    public override void Render(CanvasDrawingSession ds, RenderContext ctx)
    {
        if (Points.Count == 0) return;
        if (Points.Count == 1)
        {
            ds.FillCircle(Points[0], EffectiveWidth / 2, EffectiveColor);
            return;
        }

        using var builder = new CanvasPathBuilder(ds);
        builder.BeginFigure(Points[0]);
        if (Points.Count == 2)
        {
            builder.AddLine(Points[1]);
        }
        else
        {
            // Quadratic curves through segment midpoints give a smooth stroke from raw mouse points.
            for (int i = 1; i < Points.Count - 1; i++)
                builder.AddQuadraticBezier(Points[i], (Points[i] + Points[i + 1]) / 2);
            builder.AddLine(Points[^1]);
        }
        builder.EndFigure(CanvasFigureLoop.Open);

        using var geometry = CanvasGeometry.CreatePath(builder);
        using var style = new CanvasStrokeStyle { StartCap = Cap, EndCap = Cap, LineJoin = CanvasLineJoin.Round };
        ds.DrawGeometry(geometry, EffectiveColor, EffectiveWidth, style);
    }

    public override bool HitTest(Vector2 p, float tolerance)
    {
        float reach = tolerance + EffectiveWidth / 2;
        if (Points.Count == 1) return Vector2.Distance(p, Points[0]) <= reach;
        for (int i = 0; i < Points.Count - 1; i++)
            if (HandleGeometry.DistanceToSegment(p, Points[i], Points[i + 1]) <= reach) return true;
        return false;
    }

    public override Annotation Clone() => CopyBaseTo(new PathAnnotation { Points = new List<Vector2>(Points) });
}

internal sealed class PenAnnotation : PathAnnotation
{
    public override Annotation Clone() => CopyBaseTo(new PenAnnotation { Points = new List<Vector2>(Points) });
}

/// <summary>Semi-transparent flat-capped stroke (drawn as one geometry so overlaps do not darken).</summary>
internal sealed class HighlighterAnnotation : PathAnnotation
{
    protected override Color EffectiveColor => Color.FromArgb(105, Color.R, Color.G, Color.B);
    protected override CanvasCapStyle Cap => CanvasCapStyle.Flat;

    public override Annotation Clone() => CopyBaseTo(new HighlighterAnnotation { Points = new List<Vector2>(Points) });
}

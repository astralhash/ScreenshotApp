using System.Numerics;
using Windows.UI;

namespace ScreenshotApp.Editor.Model;

/// <summary>Plain-data form of an annotation for the on-disk clip store. Add a case here for every new annotation type.</summary>
internal sealed class AnnotationDto
{
    public string Type { get; set; } = "";
    public Guid Id { get; set; }
    public uint Color { get; set; } // ARGB
    public float StrokeWidth { get; set; }
    public bool Fill { get; set; }

    /// <summary>Rectangle: ax,ay,bx,by. Line/Arrow: x0,y0,x1,y1. Pen/Highlighter: x,y pairs.</summary>
    public float[] Points { get; set; } = Array.Empty<float>();

    /// <summary>Text annotations: the text. Blur annotations: "Pixelate" or "Blur".</summary>
    public string? Text { get; set; }

    /// <summary>Step markers: the displayed number. Points holds the center.</summary>
    public int Number { get; set; }
}

internal static class AnnotationSerializer
{
    public static AnnotationDto ToDto(Annotation a)
    {
        var dto = new AnnotationDto
        {
            Id = a.Id,
            Color = (uint)(a.Color.A << 24 | a.Color.R << 16 | a.Color.G << 8 | a.Color.B),
            StrokeWidth = a.StrokeWidth,
            Fill = a.Fill,
        };
        switch (a)
        {
            case RectangleAnnotation r:
                dto.Type = "rect";
                dto.Points = new[] { r.A.X, r.A.Y, r.B.X, r.B.Y };
                break;
            case ArrowAnnotation l:
                dto.Type = "arrow";
                dto.Points = new[] { l.P0.X, l.P0.Y, l.P1.X, l.P1.Y };
                break;
            case LineAnnotation l:
                dto.Type = "line";
                dto.Points = new[] { l.P0.X, l.P0.Y, l.P1.X, l.P1.Y };
                break;
            case BlurAnnotation b:
                dto.Type = "blur";
                dto.Points = new[] { b.A.X, b.A.Y, b.B.X, b.B.Y };
                dto.Text = b.Mode.ToString();
                break;
            case StepAnnotation st:
                dto.Type = "step";
                dto.Points = new[] { st.Center.X, st.Center.Y };
                dto.Number = st.Number;
                break;
            case TextAnnotation t:
                dto.Type = "text";
                dto.Points = new[] { t.Position.X, t.Position.Y };
                dto.Text = t.Text;
                break;
            case HighlighterAnnotation p:
                dto.Type = "highlighter";
                dto.Points = Flatten(p.Points);
                break;
            case PenAnnotation p:
                dto.Type = "pen";
                dto.Points = Flatten(p.Points);
                break;
            default:
                throw new NotSupportedException($"No serializer for {a.GetType().Name}.");
        }
        return dto;
    }

    /// <summary>Returns null for an unknown type or malformed data, so one bad entry cannot lose the whole clip.</summary>
    public static Annotation? FromDto(AnnotationDto dto)
    {
        var pts = dto.Points;
        Annotation? a = dto.Type switch
        {
            "blur" when pts.Length >= 4 => new BlurAnnotation
            {
                A = new(pts[0], pts[1]), B = new(pts[2], pts[3]),
                Mode = Enum.TryParse<BlurMode>(dto.Text, out var mode) ? mode : BlurMode.Pixelate,
            },
            "step" when pts.Length >= 2 => new StepAnnotation { Center = new(pts[0], pts[1]), Number = dto.Number },
            "text" when pts.Length >= 2 => new TextAnnotation { Position = new(pts[0], pts[1]), Text = dto.Text ?? "" },
            "rect" when pts.Length >= 4 => new RectangleAnnotation { A = new(pts[0], pts[1]), B = new(pts[2], pts[3]) },
            "arrow" when pts.Length >= 4 => new ArrowAnnotation { P0 = new(pts[0], pts[1]), P1 = new(pts[2], pts[3]) },
            "line" when pts.Length >= 4 => new LineAnnotation { P0 = new(pts[0], pts[1]), P1 = new(pts[2], pts[3]) },
            "pen" => new PenAnnotation { Points = Expand(pts) },
            "highlighter" => new HighlighterAnnotation { Points = Expand(pts) },
            _ => null,
        };
        if (a is null) return null;

        a.Id = dto.Id;
        a.Color = Color.FromArgb((byte)(dto.Color >> 24), (byte)(dto.Color >> 16), (byte)(dto.Color >> 8), (byte)dto.Color);
        a.StrokeWidth = dto.StrokeWidth;
        a.Fill = dto.Fill;
        return a;
    }

    private static float[] Flatten(List<Vector2> points)
    {
        var flat = new float[points.Count * 2];
        for (int i = 0; i < points.Count; i++)
        {
            flat[i * 2] = points[i].X;
            flat[i * 2 + 1] = points[i].Y;
        }
        return flat;
    }

    private static List<Vector2> Expand(float[] flat)
    {
        var points = new List<Vector2>(flat.Length / 2);
        for (int i = 0; i + 1 < flat.Length; i += 2) points.Add(new Vector2(flat[i], flat[i + 1]));
        return points;
    }
}

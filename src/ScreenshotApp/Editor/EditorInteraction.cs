using System.Drawing;
using System.Numerics;
using ScreenshotApp.Editor.Model;
using Color = Windows.UI.Color;

namespace ScreenshotApp.Editor;

internal enum ToolKind { Select, Arrow, Rectangle, Line, Pen, Highlighter, Text, Step, Blur, Crop }

/// <summary>
/// Tool, selection and drag logic in image-pixel coordinates. The window translates pointer input into
/// these calls, so nothing here depends on WinUI input types.
/// </summary>
internal sealed class EditorInteraction
{
    private enum Mode { None, Create, Move, Resize, CropNew, CropResize }

    private const float HandleRadiusDip = 8, HitToleranceDip = 6, MinShapeSize = 3, MinCropSize = 8;

    private readonly List<Vector2> _rawPoints = new();
    private Mode _mode;
    private int _handle;
    private Vector2 _start, _last;
    private Annotation? _before;
    private bool _changed;
    private EditorDocument? _doc;
    private Guid? _selectedId;
    private ToolKind _tool = ToolKind.Arrow;
    private RectangleF? _cropBeforeDrag;

    public ToolKind Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            _mode = Mode.None;
            Preview = null;
            if (value == ToolKind.Crop) BeginCrop(); else PendingCrop = null;
            Changed?.Invoke();
        }
    }

    // Style used for new shapes (and pushed to the selection by the view model).
    public Color StrokeColor { get; set; } = Color.FromArgb(255, 229, 57, 53);
    public float StrokeWidth { get; set; } = 6;
    public bool Fill { get; set; }
    public BlurMode BlurMode { get; set; } = BlurMode.Pixelate;

    /// <summary>Screen DIPs per image pixel; converts the fixed-size hit areas to image space.</summary>
    public float DipsPerPixel { get; set; } = 1;

    /// <summary>The shape being drawn right now (not yet in the document).</summary>
    public Annotation? Preview { get; private set; }

    /// <summary>The crop being adjusted while the Crop tool is active (image pixels).</summary>
    public RectangleF? PendingCrop { get; private set; }

    /// <summary>A text annotation currently being edited in place; the canvas must not draw it.</summary>
    public Guid? EditingId { get; set; }

    public event Action? Changed;
    public event Action? SelectionChanged;

    /// <summary>Asks the window to show an in-place editor. The bool says whether the text is new.</summary>
    public event Action<TextAnnotation, bool>? TextEditRequested;

    public EditorDocument? Doc
    {
        get => _doc;
        set
        {
            _doc = value;
            _mode = Mode.None;
            Preview = null;
            EditingId = null;
            PendingCrop = null;
            if (_tool == ToolKind.Crop) BeginCrop();
            SetSelected(null);
            Changed?.Invoke();
        }
    }

    public Annotation? Selected =>
        _selectedId is { } id ? _doc?.Annotations.FirstOrDefault(a => a.Id == id) : null;

    public bool IsDragging => _mode != Mode.None;

    public void RaiseChanged() => Changed?.Invoke();

    private void SetSelected(Guid? id)
    {
        if (_selectedId == id) return;
        _selectedId = id;
        SelectionChanged?.Invoke();
    }

    // ---- pointer ----

    public void PointerDown(Vector2 p, bool shift)
    {
        if (_doc is null) return;

        if (Tool == ToolKind.Crop)
        {
            CropPointerDown(p);
            return;
        }

        if (Selected is { } sel && HitHandle(sel, p) is var h and >= 0)
        {
            _mode = Mode.Resize;
            _handle = h;
            _before = sel.Clone();
            _changed = false;
            return;
        }

        if (Tool == ToolKind.Select)
        {
            var hit = HitTestTop(p);
            SetSelected(hit?.Id);
            if (hit is not null)
            {
                _mode = Mode.Move;
                _before = hit.Clone();
                _last = p;
                _changed = false;
            }
            Changed?.Invoke();
            return;
        }

        if (Tool == ToolKind.Text)
        {
            // Click on existing text edits it, otherwise starts a new label here.
            if (HitTestTop(p, onlyText: true) is TextAnnotation existing)
            {
                SetSelected(existing.Id);
                TextEditRequested?.Invoke(existing, false);
            }
            else
            {
                SetSelected(null);
                TextEditRequested?.Invoke(NewText(p), true);
            }
            Changed?.Invoke();
            return;
        }

        SetSelected(null);
        _mode = Mode.Create;
        _start = p;
        Preview = CreateAnnotation(p);
        Changed?.Invoke();
    }

    public void PointerMove(Vector2 p, bool shift)
    {
        switch (_mode)
        {
            case Mode.Move when Selected is { } sel:
                if (p != _last)
                {
                    sel.Move(p - _last);
                    _last = p;
                    _changed = true;
                }
                break;
            case Mode.Resize when Selected is { } sel:
                sel.SetHandle(_handle, p, shift);
                _changed = true;
                break;
            case Mode.Create when Preview is not null:
                UpdatePreview(p, shift);
                break;
            case Mode.CropNew:
                PendingCrop = Normalize(_start, ClampToImage(p));
                break;
            case Mode.CropResize when PendingCrop is { } crop:
                PendingCrop = HandleGeometry.Apply(crop, _handle, ClampToImage(p), MinCropSize);
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }

    public void PointerUp(Vector2 p, bool shift)
    {
        if (_mode == Mode.None) return;
        PointerMove(p, shift);

        var mode = _mode;
        _mode = Mode.None;

        if (mode is Mode.Move or Mode.Resize)
        {
            if (_changed && _before is not null && Selected is { } sel && _doc is not null)
                _doc.Commands.Push(new ReplaceCommand(_doc.Annotations, _before, sel));
            _before = null;
        }
        else if (mode == Mode.Create && Preview is { } created && _doc is not null)
        {
            Preview = null;
            if (!IsDegenerate(created))
            {
                _doc.Commands.Execute(new AddCommand(_doc.Annotations, created));
                SetSelected(created.Id);
            }
        }
        else if (mode == Mode.CropNew && PendingCrop is { } crop
                 && (crop.Width < MinCropSize || crop.Height < MinCropSize))
        {
            PendingCrop = _cropBeforeDrag; // a click without a drag keeps the previous crop box
        }
        Changed?.Invoke();
    }

    /// <summary>What the cursor should look like at <paramref name="p"/> when nothing is being dragged.</summary>
    public CursorKind HoverCursor(Vector2 p)
    {
        if (_doc is null) return CursorKind.Arrow;
        if (Tool == ToolKind.Crop)
        {
            if (PendingCrop is { } crop && HitCropHandle(crop, p) is var ch and >= 0) return HandleGeometry.Cursor(ch);
            return CursorKind.Cross;
        }
        if (Selected is { } sel && HitHandle(sel, p) is var h and >= 0) return sel.GetHandleCursor(h);
        if (Tool == ToolKind.Text) return HitTestTop(p, onlyText: true) is not null ? CursorKind.Move : CursorKind.Cross;
        if (Tool != ToolKind.Select) return CursorKind.Cross;
        return HitTestTop(p) is not null ? CursorKind.Move : CursorKind.Arrow;
    }

    // ---- text ----

    /// <summary>Double-click with any tool: edit the text label under the pointer, if there is one.</summary>
    public bool TryEditTextAt(Vector2 p)
    {
        if (_doc is null || HitTestTop(p, onlyText: true) is not TextAnnotation text) return false;
        SetSelected(text.Id);
        TextEditRequested?.Invoke(text, false);
        return true;
    }

    private TextAnnotation NewText(Vector2 p) => new()
    {
        Position = p,
        Color = StrokeColor,
        StrokeWidth = StrokeWidth,
    };

    /// <summary>Applies the result of the in-place editor: add, change or (when emptied) delete.</summary>
    public void CommitText(TextAnnotation original, string text, bool isNew)
    {
        EditingId = null;
        if (_doc is null) return;

        text = text.TrimEnd('\r', '\n');
        if (isNew)
        {
            if (text.Length > 0)
            {
                var added = (TextAnnotation)original.Clone();
                added.Text = text;
                _doc.Commands.Execute(new AddCommand(_doc.Annotations, added));
                SetSelected(added.Id);
            }
        }
        else if (text.Length == 0)
        {
            _doc.Commands.Execute(new RemoveCommand(_doc.Annotations, original));
            SetSelected(null);
        }
        else if (text != original.Text)
        {
            var changed = (TextAnnotation)original.Clone();
            changed.Text = text;
            _doc.Commands.Execute(new ReplaceCommand(_doc.Annotations, original, changed));
        }
        Changed?.Invoke();
    }

    // ---- crop ----

    private void BeginCrop()
    {
        if (_doc is null) return;
        SetSelected(null);
        PendingCrop = _doc.Crop.Rect ?? new RectangleF(0, 0, _doc.Width, _doc.Height);
    }

    private void CropPointerDown(Vector2 p)
    {
        if (PendingCrop is { } crop && HitCropHandle(crop, p) is var h and >= 0)
        {
            _mode = Mode.CropResize;
            _handle = h;
            return;
        }
        _cropBeforeDrag = PendingCrop;
        _mode = Mode.CropNew;
        _start = ClampToImage(p);
        PendingCrop = new RectangleF(_start.X, _start.Y, 0, 0);
        Changed?.Invoke();
    }

    private int HitCropHandle(RectangleF crop, Vector2 p)
    {
        float radius = HandleRadiusDip / Math.Max(DipsPerPixel, 0.01f);
        for (int i = 0; i < 8; i++)
            if (Vector2.Distance(HandleGeometry.Get(crop, i), p) <= radius) return i;
        return -1;
    }

    private Vector2 ClampToImage(Vector2 p) =>
        _doc is null ? p : new Vector2(Math.Clamp(p.X, 0, _doc.Width), Math.Clamp(p.Y, 0, _doc.Height));

    private static RectangleF Normalize(Vector2 a, Vector2 b) =>
        RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Commits the pending crop (undoable). A box covering the whole image removes the crop.</summary>
    public bool ApplyCrop()
    {
        if (_doc is null || PendingCrop is not { } r || r.Width < MinCropSize || r.Height < MinCropSize) return false;

        var whole = new RectangleF(0, 0, _doc.Width, _doc.Height);
        // Whole pixels only, so the exported size is exact.
        var snapped = RectangleF.FromLTRB(MathF.Floor(r.Left), MathF.Floor(r.Top), MathF.Ceiling(r.Right), MathF.Ceiling(r.Bottom));
        RectangleF? after = snapped == whole ? null : snapped;

        if (_doc.Crop.Rect != after)
            _doc.Commands.Execute(new CropCommand(_doc.Crop, _doc.Crop.Rect, after));
        return true;
    }

    // ---- selection actions ----

    public void DeleteSelected()
    {
        if (_doc is null || Selected is not { } sel) return;
        _doc.Commands.Execute(new RemoveCommand(_doc.Annotations, sel));
        SetSelected(null);
        Changed?.Invoke();
    }

    /// <summary>Removes all annotations and the crop (one undo step), leaving the original capture.</summary>
    public void ClearAll()
    {
        if (_doc is null || (_doc.Annotations.Count == 0 && _doc.Crop.Rect is null)) return;
        _doc.Commands.Execute(new ClearCommand(_doc.Annotations, _doc.Crop));
        SetSelected(null);
        if (Tool == ToolKind.Crop) BeginCrop();
        Changed?.Invoke();
    }

    public void Deselect()
    {
        SetSelected(null);
        Changed?.Invoke();
    }

    /// <summary>Applies the current style to the selected annotation as one (mergeable) undo step.</summary>
    public void ApplyStyleToSelection()
    {
        if (_doc is null || Selected is not { } sel) return;
        bool modeDiffers = sel is BlurAnnotation blur && blur.Mode != BlurMode;
        if (!modeDiffers && sel.Color == StrokeColor && sel.StrokeWidth == StrokeWidth
            && (!sel.SupportsFill || sel.Fill == Fill)) return;

        var before = sel.Clone();
        sel.Color = StrokeColor;
        sel.StrokeWidth = StrokeWidth;
        if (sel.SupportsFill) sel.Fill = Fill;
        if (sel is BlurAnnotation b) b.Mode = BlurMode;
        _doc.Commands.Push(new ReplaceCommand(_doc.Annotations, before, sel, mergeKey: "style"));
        Changed?.Invoke();
    }

    // ---- internals ----

    private float Tolerance => HitToleranceDip / Math.Max(DipsPerPixel, 0.01f);

    private Annotation? HitTestTop(Vector2 p, bool onlyText = false)
    {
        if (_doc is null) return null;
        for (int i = _doc.Annotations.Count - 1; i >= 0; i--)
        {
            var a = _doc.Annotations[i];
            if (onlyText && a is not TextAnnotation) continue;
            if (a.HitTest(p, Tolerance)) return a;
        }
        return null;
    }

    private int HitHandle(Annotation a, Vector2 p)
    {
        float radius = HandleRadiusDip / Math.Max(DipsPerPixel, 0.01f);
        for (int i = 0; i < a.HandleCount; i++)
            if (Vector2.Distance(a.GetHandle(i), p) <= radius) return i;
        return -1;
    }

    private int NextStepNumber() =>
        (_doc?.Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() ?? 0) + 1;

    private Annotation CreateAnnotation(Vector2 p)
    {
        Annotation a;
        switch (Tool)
        {
            case ToolKind.Rectangle: a = new RectangleAnnotation { A = p, B = p }; break;
            case ToolKind.Blur: a = new BlurAnnotation { A = p, B = p, Mode = BlurMode }; break;
            case ToolKind.Step: a = new StepAnnotation { Center = p, Number = NextStepNumber() }; break;
            case ToolKind.Line: a = new LineAnnotation { P0 = p, P1 = p }; break;
            case ToolKind.Pen:
                a = new PenAnnotation { Points = { p } };
                _rawPoints.Clear();
                _rawPoints.Add(p);
                break;
            case ToolKind.Highlighter:
                a = new HighlighterAnnotation { Points = { p } };
                _rawPoints.Clear();
                _rawPoints.Add(p);
                break;
            default: a = new ArrowAnnotation { P0 = p, P1 = p }; break;
        }
        a.Color = StrokeColor;
        a.StrokeWidth = StrokeWidth;
        a.Fill = a.SupportsFill && Fill;
        return a;
    }

    private void UpdatePreview(Vector2 p, bool shift)
    {
        switch (Preview)
        {
            case BoxAnnotation box:
                box.SetDrag(_start, p, shift);
                break;
            case StepAnnotation step:
                step.Center = p; // drag to position the marker, release to place it
                break;
            case LineAnnotation line:
                line.P0 = _start;
                line.P1 = shift ? HandleGeometry.SnapAngle(_start, p) : p;
                break;
            case PathAnnotation path:
                // Raw points are always collected, so Shift can switch to/from free-draw mid-stroke.
                if (Vector2.Distance(_rawPoints[^1], p) >= 1.5f) _rawPoints.Add(p);
                // Pen: Shift draws a straight line. Highlighter is the exception: straight by default,
                // Shift switches it to free-draw.
                bool straight = path is HighlighterAnnotation ? !shift : shift;
                path.Points = straight
                    ? new List<Vector2> { _start, HandleGeometry.SnapAngle(_start, p) }
                    : new List<Vector2>(_rawPoints);
                break;
        }
    }

    private static bool IsDegenerate(Annotation a) => a switch
    {
        BoxAnnotation b => Math.Max(b.Box.Width, b.Box.Height) < MinShapeSize,
        LineAnnotation l => Vector2.Distance(l.P0, l.P1) < MinShapeSize,
        PathAnnotation { Points.Count: 2 } p => Vector2.Distance(p.Points[0], p.Points[1]) < MinShapeSize,
        PathAnnotation p => p.Points.Count < 2,
        _ => false,
    };
}

using System.Drawing;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenshotApp.Capture;

internal enum OverlayMode { Region, Window }

/// <summary>
/// Freezes the virtual screen, shows one <see cref="OverlayWindow"/> per monitor and runs the selection
/// interaction. Resolves to the cropped image, or null when cancelled.
/// </summary>
internal sealed class CaptureOverlay
{
    private const int MinSelection = 3;

    private readonly OverlayMode _mode;
    private readonly TaskCompletionSource<CapturedImage?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<OverlayWindow> _overlays = new();
    private CapturedImage _frozen = null!;
    private Rectangle _virtual;
    private List<Rectangle> _windows = new();
    private Point? _dragStart;
    private bool _done;

    public static bool IsActive { get; private set; }

    public Rectangle? Selection { get; private set; }
    public Point Cursor { get; private set; }
    public bool ShowSize => Selection is { Width: > 0, Height: > 0 };

    /// <summary>The Z-key magnifier (region capture only).</summary>
    public bool LensEnabled { get; private set; }

    public CaptureOverlay(OverlayMode mode) => _mode = mode;

    public Task<CapturedImage?> RunAsync()
    {
        IsActive = true;
        try
        {
            // Enumerate windows first: the overlays are about to cover everything.
            if (_mode == OverlayMode.Window) _windows = WindowEnumerator.GetVisibleWindowBounds();
            _virtual = Monitors.VirtualScreen;
            _frozen = ScreenCapture.CaptureRect(_virtual);

            PInvoke.GetCursorPos(out var pt);
            Cursor = new Point(pt.X, pt.Y);
            UpdateHover();

            var monitors = Monitors.GetAll();
            foreach (var m in monitors)
                _overlays.Add(new OverlayWindow(this, m, _frozen, pollEsc: _overlays.Count == 0));

            var active = _overlays.FirstOrDefault(o => monitors[_overlays.IndexOf(o)].Contains(Cursor)) ?? _overlays[0];
            active.Activate();
            InvalidateAll();
        }
        catch
        {
            Teardown();
            throw;
        }
        return _tcs.Task;
    }

    // ---- input from overlays (all points are virtual-screen physical pixels) ----

    public void OnMouseMove(Point p)
    {
        Cursor = Clamp(p);
        if (_mode == OverlayMode.Window) UpdateHover();
        else if (_dragStart is { } start) Selection = Normalize(start, Cursor);
        InvalidateAll();
    }

    public void OnMouseDown(Point p)
    {
        Cursor = Clamp(p);
        if (_mode == OverlayMode.Region)
        {
            _dragStart = Cursor;
            Selection = null;
            InvalidateAll();
        }
    }

    public void OnMouseUp(Point p)
    {
        Cursor = Clamp(p);
        if (_mode == OverlayMode.Window)
        {
            UpdateHover();
            Confirm();
            return;
        }
        if (_dragStart is null) return;
        Selection = Normalize(_dragStart.Value, Cursor);
        _dragStart = null;
        if (Selection is { Width: >= MinSelection, Height: >= MinSelection }) Confirm();
        else
        {
            Selection = null; // a click without a drag: keep waiting
            InvalidateAll();
        }
    }

    public void ToggleLens()
    {
        if (_mode != OverlayMode.Region) return;
        LensEnabled = !LensEnabled;
        InvalidateAll();
    }

    /// <summary>Arrow keys move the cursor one pixel, so the lens can be used to aim exactly.</summary>
    public void Nudge(int dx, int dy)
    {
        if (_mode != OverlayMode.Region) return;
        PInvoke.GetCursorPos(out var pt);
        var moved = Clamp(new Point(pt.X + dx, pt.Y + dy));
        PInvoke.SetCursorPos(moved.X, moved.Y);
        OnMouseMove(moved); // do not rely on the system echoing a mouse-move message
    }

    public void Confirm()
    {
        if (_done) return;
        if (Selection is { } sel && sel.Width >= 1 && sel.Height >= 1) Finish(Crop(sel));
    }

    public void Cancel()
    {
        if (!_done) Finish(null);
    }

    // ---- internals ----

    private void UpdateHover()
    {
        Selection = null;
        foreach (var w in _windows)
        {
            if (!w.Contains(Cursor)) continue;
            var r = Rectangle.Intersect(w, _virtual);
            if (!r.IsEmpty) Selection = r;
            return;
        }
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, _virtual.Left, _virtual.Right - 1),
        Math.Clamp(p.Y, _virtual.Top, _virtual.Bottom - 1));

    private static Rectangle Normalize(Point a, Point b)
    {
        // Inclusive of the pixel under the cursor, like the system snipping tools.
        int l = Math.Min(a.X, b.X), t = Math.Min(a.Y, b.Y);
        int r = Math.Max(a.X, b.X), btm = Math.Max(a.Y, b.Y);
        return Rectangle.FromLTRB(l, t, r + 1, btm + 1);
    }

    private void InvalidateAll()
    {
        foreach (var o in _overlays) o.Invalidate();
    }

    private CapturedImage Crop(Rectangle sel)
    {
        sel = Rectangle.Intersect(sel, _virtual);
        int w = sel.Width, h = sel.Height;
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int srcOffset = ((sel.Y - _frozen.ScreenY + y) * _frozen.Width + (sel.X - _frozen.ScreenX)) * 4;
            Buffer.BlockCopy(_frozen.Pixels, srcOffset, pixels, y * w * 4, w * 4);
        }
        return new CapturedImage(pixels, w, h, sel.X, sel.Y);
    }

    private void Finish(CapturedImage? result)
    {
        _done = true;
        Teardown();
        _tcs.TrySetResult(result);
    }

    private void Teardown()
    {
        IsActive = false;
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }
}

using System.Drawing;
using ScreenshotApp.Services;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenshotApp.Capture;

internal enum OverlayMode { Region, Window, Scroll }

/// <summary>
/// Freezes the virtual screen, shows one <see cref="OverlayWindow"/> per monitor and runs the selection
/// interaction. Resolves to the cropped image, or null when cancelled.
/// </summary>
internal sealed class CaptureOverlay
{
    private const int MinSelection = 3;

    private readonly OverlayMode _mode;
    private readonly bool _lensAllowed;
    private readonly TaskCompletionSource<CapturedImage?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<OverlayWindow> _overlays = new();
    private CapturedImage _frozen = null!;
    private Rectangle _virtual;
    private List<WindowInfo> _windows = new();
    private int _hoverIndex = -1; // index into _windows of the window under the cursor (window mode)
    private Point? _dragStart;
    private bool _done;
    private ScrollTarget? _scrollHit;           // scroll mode: the scrollable area last found under the cursor
    private bool _lookupBusy, _lookupPending;

    /// <summary>Scroll mode: the area that was clicked (the caller owns and disposes it). Null otherwise.</summary>
    public ScrollTarget? PickedScroll { get; private set; }

    public static bool IsActive { get; private set; }

    public Rectangle? Selection { get; private set; }
    public Point Cursor { get; private set; }
    public bool ShowSize => Selection is { Width: > 0, Height: > 0 };

    /// <summary>The Z-key magnifier (region capture only).</summary>
    public bool LensEnabled { get; private set; }

    /// <summary>True after C copied the colour; cleared when the cursor moves.</summary>
    public bool ColorCopied { get; private set; }

    /// <summary>Darken the frozen screen outside the selection.</summary>
    public bool Dim { get; }

    /// <summary>Draw crosshair lines through the cursor.</summary>
    public bool Crosshair { get; }

    /// <param name="allowLens">False to keep the zoom lens off (and Z inert) whatever the saved setting, e.g. when
    /// the overlay only picks a region to share.</param>
    /// <param name="dim">See <see cref="Dim"/>; off by setting, and while a region is shared (viewers would see it).</param>
    /// <param name="crosshair">See <see cref="Crosshair"/>; off while a region is shared.</param>
    public CaptureOverlay(OverlayMode mode, bool allowLens = true, bool dim = true, bool crosshair = true)
    {
        _mode = mode;
        Dim = dim;
        Crosshair = crosshair;
        _lensAllowed = mode == OverlayMode.Region && allowLens;
        LensEnabled = _lensAllowed && App.Settings.Current.ZoomLensEnabled;
    }

    public Task<CapturedImage?> RunAsync()
    {
        IsActive = true;
        try
        {
            // Enumerate windows first: the overlays are about to cover everything.
            if (_mode != OverlayMode.Region) _windows = WindowEnumerator.GetVisibleWindows();
            _virtual = Monitors.VirtualScreen;
            _frozen = ScreenCapture.CaptureRect(_virtual);

            PInvoke.GetCursorPos(out var pt);
            Cursor = new Point(pt.X, pt.Y);
            UpdateHover();
            if (_mode == OverlayMode.Scroll) RequestScrollLookup();

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
        var moved = Clamp(p) != Cursor;
        Cursor = Clamp(p);
        if (moved) ColorCopied = false;
        if (_mode != OverlayMode.Region)
        {
            UpdateHover();
            if (_mode == OverlayMode.Scroll && moved) RequestScrollLookup();
        }
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
        if (_mode != OverlayMode.Region)
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
        if (!_lensAllowed) return;
        LensEnabled = !LensEnabled;
        App.Settings.Current.ZoomLensEnabled = LensEnabled;
        try { App.Settings.Save(); }
        catch (Exception ex) { Log.Error(ex); }
        InvalidateAll();
    }

    /// <summary>Copies the colour under the cursor as #RRGGBB (lens only).</summary>
    public void CopyColor()
    {
        if (_mode != OverlayMode.Region || !LensEnabled) return;
        int i = ((Cursor.Y - _frozen.ScreenY) * _frozen.Width + (Cursor.X - _frozen.ScreenX)) * 4;
        var px = _frozen.Pixels;
        try
        {
            ClipboardService.CopyText($"#{px[i + 2]:X2}{px[i + 1]:X2}{px[i]:X2}");
            ColorCopied = true;
        }
        catch (Exception ex) { Log.Error(ex); }
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
        if (Selection is not { Width: >= 1, Height: >= 1 } sel) return;
        if (_mode == OverlayMode.Scroll)
        {
            if (_hoverIndex >= 0) FinishScroll(_windows[_hoverIndex].Handle, Cursor);
            return;
        }
        if (_mode == OverlayMode.Window && _hoverIndex >= 0) FinishWindow(sel, _hoverIndex);
        else Finish(Crop(sel));
    }

    public void Cancel()
    {
        if (!_done) Finish(null);
    }

    // ---- internals ----

    private void UpdateHover()
    {
        Selection = null;
        _hoverIndex = -1;
        for (int i = 0; i < _windows.Count; i++)
        {
            if (!_windows[i].Bounds.Contains(Cursor)) continue;
            var r = Rectangle.Intersect(_windows[i].Bounds, _virtual);
            if (!r.IsEmpty)
            {
                Selection = r;
                _hoverIndex = i;
                // Scroll mode: the scrollable area under the cursor once it is known, else the whole window.
                if (_scrollHit is { } hit && hit.Window == _windows[i].Handle && hit.Area.Contains(Cursor))
                    Selection = Rectangle.Intersect(hit.Area, _virtual);
            }
            return;
        }
    }

    /// <summary>
    /// Looks up the scrollable area under the cursor in the background (UI Automation calls are slow and
    /// cross-process). One lookup at a time; moves meanwhile trigger one more for the latest position.
    /// </summary>
    private async void RequestScrollLookup()
    {
        if (_hoverIndex < 0 || _done) return;
        if (_lookupBusy)
        {
            _lookupPending = true;
            return;
        }
        _lookupBusy = true;
        try
        {
            do
            {
                _lookupPending = false;
                if (_hoverIndex < 0) break;
                var hwnd = _windows[_hoverIndex].Handle;
                var point = Cursor;
                var hit = await Task.Run(() => ScrollAutomation.Find(hwnd, point));
                if (_done)
                {
                    hit.Dispose();
                    break;
                }
                _scrollHit?.Dispose();
                _scrollHit = hit;
                UpdateHover();
                InvalidateAll();
            } while (_lookupPending);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        finally
        {
            _lookupBusy = false;
        }
    }

    /// <summary>Scroll mode: resolves the area at the click (reusing the hover lookup when it covers the point).</summary>
    private async void FinishScroll(Windows.Win32.Foundation.HWND hwnd, Point point)
    {
        _done = true;
        Teardown();
        ScrollTarget? target = null;
        try
        {
            if (_scrollHit is { } hit && hit.Window == hwnd && hit.Area.Contains(point))
            {
                target = hit;
                _scrollHit = null;
            }
            else
            {
                target = await Task.Run(() => ScrollAutomation.Find(hwnd, point));
            }
        }
        catch (Exception ex) { Log.Error(ex); }
        _scrollHit?.Dispose();
        _scrollHit = null;
        if (target is null || target.Area.Width < 1 || target.Area.Height < 1)
        {
            target?.Dispose();
            _tcs.TrySetResult(null);
            return;
        }
        PickedScroll = target;
        _tcs.TrySetResult(Crop(target.Area));
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

    /// <summary>
    /// Window mode: grabs the window itself (Windows.Graphics.Capture), so windows in front of it are not in
    /// the shot. Falls back to the frozen screen when that is unavailable or fails.
    /// </summary>
    private async void FinishWindow(Rectangle sel, int index)
    {
        _done = true;
        Teardown();
        var frozen = Crop(sel);
        CapturedImage? image = null;
        try
        {
            // Windows above the target in z-order: the frozen pixels there are not what lies behind the window.
            var occluders = _windows.Take(index).Select(w => Rectangle.Intersect(w.Bounds, sel))
                .Where(r => !r.IsEmpty).ToList();
            image = await WindowCapture.TryCaptureAsync(_windows[index].Handle, frozen, occluders);
        }
        catch (Exception ex) { Log.Error(ex); }
        _tcs.TrySetResult(image ?? frozen);
    }

    private void Finish(CapturedImage? result)
    {
        _done = true;
        Teardown();
        _scrollHit?.Dispose();
        _scrollHit = null;
        _tcs.TrySetResult(result);
    }

    private void Teardown()
    {
        IsActive = false;
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }
}

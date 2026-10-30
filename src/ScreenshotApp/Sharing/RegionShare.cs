using System.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using ScreenshotApp.Capture;
using Windows.Win32;

namespace ScreenshotApp.Sharing;

/// <summary>
/// "Share a region" for apps that can only share windows: live frames of the region shown in a hidden
/// <see cref="MirrorWindow"/> (picked in the sharing app), with an outline marking the area on screen. UI thread only.
/// </summary>
internal sealed class RegionShare : IDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly MirrorWindow _mirror;
    private RegionFrameSource _source;
    private RegionOutline _outline;
    private readonly ShareToolbar _toolbar = new();
    private readonly ResizeGrip _grip = new();
    private readonly DispatcherQueueTimer _hoverTimer;
    private long _lastNear;   // Environment.TickCount64 when the cursor was last on the frame or toolbar
    private long _ringSince;  // ... when the cursor came onto the frame (0: it is not on it)
    private bool _disposed;

    private const int HoverDelayMs = 200;      // the cursor must rest on the frame this long before the resize arrows and toolbar appear (together)
    private const int HoverGraceMs = 400;       // the toolbar stays this long after the cursor leaves
    private const int ReanchorDistance = 150;   // physical pixels along the frame before the toolbar follows the cursor

    /// <summary>The share ended by itself or from the mirror window; the owner should dispose it.</summary>
    public event Action? Ended;

    /// <summary>The region was moved or resized (from the toolbar or <see cref="ChangeRegion"/>).</summary>
    public event Action? RegionChanged;

    /// <summary>The hidden window to pick in the sharing app.</summary>
    public MirrorWindow Mirror => _mirror;

    /// <summary>The shared screen area (physical pixels).</summary>
    public Rectangle Region => _source.Region;

    public RegionShare(Rectangle region)
    {
        _mirror = new MirrorWindow(CanvasDevice.GetSharedDevice()); // the device the frame source uses
        try
        {
            _source = StartSource(region);
            _outline = new RegionOutline(_source.Region);
        }
        catch
        {
            _source?.Dispose();
            _toolbar.Dispose();
            _grip.Dispose();
            _mirror.CloseWindow();
            throw;
        }
        _mirror.StopRequested += () => Ended?.Invoke();
        _mirror.ShowFor(_source.Region);
        _outline.Show(true);

        _toolbar.RegionDragging += r => _outline.SetRegion(r);
        _toolbar.RegionDropped += OnDropped;
        _grip.RegionDragging += r =>
        {
            _outline.SetRegion(r);
            _toolbar.Hide(); // it would sit where the frame was
        };
        _grip.RegionDropped += OnDropped;
        // Not from inside the toolbar's own message handler: ending the share destroys the toolbar window.
        _toolbar.CloseRequested += () => _dispatcher.TryEnqueue(() => { if (!_disposed) Ended?.Invoke(); });
        _hoverTimer = _dispatcher.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(50);
        _hoverTimer.Tick += (_, _) => CheckHover();
        _hoverTimer.Start();
    }

    public void ChangeRegion(Rectangle region)
    {
        var source = StartSource(region); // throws before anything is replaced
        _source.Dispose();
        _source = source;
        _outline.SetRegion(source.Region);
        _mirror.SetRegion(source.Region);
        _grip.Hide(); // its ring is around the old frame; the hover check puts it back
        RegionChanged?.Invoke();
    }

    /// <summary>A drag of the toolbar's move handle or of a frame edge ended: switch the share to the new region.</summary>
    private void OnDropped(Rectangle region)
    {
        // On success the toolbar, if it moved the region, already sits where the drag left it.
        try
        {
            ChangeRegion(region);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
            _outline.SetRegion(Region); // keep showing what is really shared
            _toolbar.Hide();
        }
        _ringSince = Environment.TickCount64; // the toolbar waits again before it shows up
        // Keep the resize arrows if the cursor is still on the (new) frame, so they do not flicker off for a moment.
        PInvoke.GetCursorPos(out var p);
        if (ShareToolbar.ZoneAt(Region, new Point(p.X, p.Y)) != Edges.None) _grip.Show(Region);
    }

    /// <summary>
    /// Hovering the frame: after a short rest the cursor turns into resize arrows (<see cref="ResizeGrip"/>), after a
    /// longer one the toolbar appears where the cursor is. Both go away when the cursor leaves.
    /// </summary>
    private void CheckHover()
    {
        if (_disposed) return;
        long now = Environment.TickCount64;
        if (_grip.IsDragging)
        {
            _lastNear = now;
            return;
        }
        // Not while the capture overlay is up: the frame tools would pop up over it.
        if (CaptureOverlay.IsActive && !_toolbar.IsDragging)
        {
            _toolbar.Hide();
            _grip.Hide();
            return;
        }

        PInvoke.GetCursorPos(out var p);
        var cursor = new Point(p.X, p.Y);
        if (_toolbar.IsDragging)
        {
            _lastNear = now;
            _grip.Hide();
            _toolbar.Refresh(cursor);
            return;
        }

        var region = Region;
        var zone = ShareToolbar.ZoneAt(region, cursor);
        bool onToolbar = _toolbar.Contains(cursor);
        if (zone != Edges.None) { if (_ringSince == 0) _ringSince = now; }
        else if (!onToolbar) _ringSince = 0;

        if (zone == Edges.None)
            _grip.Hide();
        else if (!_grip.Visible && now - _ringSince >= HoverDelayMs)
        {
            _grip.Show(region);
            if (_toolbar.Visible) _toolbar.Raise(); // the toolbar stays above the ring where they overlap
        }

        if (zone != Edges.None || onToolbar)
        {
            _lastNear = now;
            // The toolbar goes where the cursor touches the frame, and follows when it moves to another side or far along.
            if (!onToolbar && zone != Edges.None)
            {
                if (_toolbar.Visible)
                {
                    if (zone != _toolbar.Zone || Distance(cursor, _toolbar.Anchor) > ReanchorDistance)
                        _toolbar.Place(region, cursor, zone);
                }
                else if (now - _ringSince >= HoverDelayMs)
                {
                    _toolbar.Place(region, cursor, zone);
                    _toolbar.Raise();
                }
            }
        }
        else if (_toolbar.Visible && now - _lastNear > HoverGraceMs)
        {
            _toolbar.Hide();
        }
        _toolbar.Refresh(cursor);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    private RegionFrameSource StartSource(Rectangle region)
    {
        var source = new RegionFrameSource(region, includeCursor: true);
        source.FrameArrived += _mirror.Draw;
        source.Stopped += () => _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && ReferenceEquals(source, _source)) Ended?.Invoke();
        });
        try { source.Start(); }
        catch
        {
            source.Dispose();
            throw;
        }
        return source;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hoverTimer.Stop();
        _toolbar.Dispose();
        _grip.Dispose();
        _source.Dispose();
        _outline.Dispose();
        _mirror.CloseWindow();
    }
}

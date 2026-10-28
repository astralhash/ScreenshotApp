using System.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using ScreenshotApp.Capture;

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
    private bool _disposed;

    /// <summary>The share ended by itself or from the mirror window; the owner should dispose it.</summary>
    public event Action? Ended;

    /// <summary>The hidden window to pick in the sharing app.</summary>
    public MirrorWindow Mirror => _mirror;

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
            _mirror.CloseWindow();
            throw;
        }
        _mirror.StopRequested += () => Ended?.Invoke();
        _mirror.ShowFor(_source.Region);
        _outline.Show(true);
    }

    /// <summary>Hides the outline, e.g. while a new region is picked (the overlay freezes the screen as it is).</summary>
    public void ShowOutline(bool visible) => _outline.Show(visible);

    public void ChangeRegion(Rectangle region)
    {
        var source = StartSource(region); // throws before anything is replaced
        _source.Dispose();
        _source = source;
        _outline.Dispose();
        _outline = new RegionOutline(source.Region);
        _outline.Show(true);
        _mirror.SetRegion(source.Region);
    }

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
        _source.Dispose();
        _outline.Dispose();
        _mirror.CloseWindow();
    }
}

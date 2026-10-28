using System.Drawing;
using System.Runtime.Versioning;
using Microsoft.Graphics.Canvas;
using ScreenshotApp.Services;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;

namespace ScreenshotApp.Capture;

/// <summary>
/// One live frame: the whole monitor as a GPU bitmap plus the part of it that is the region (<see cref="Source"/>,
/// in bitmap pixels). Only valid during the <see cref="RegionFrameSource.FrameArrived"/> callback.
/// </summary>
internal readonly record struct RegionFrame(CanvasBitmap Bitmap, Windows.Foundation.Rect Source, TimeSpan Timestamp);

/// <summary>
/// Live frames of a screen region: Windows.Graphics.Capture of the monitor that holds it, cropped by the consumer
/// on the GPU. The shared engine for the screen-share mirror and (not yet built) region recording, which would
/// subscribe to <see cref="FrameArrived"/> and feed an encoder. A region is limited to one monitor.
/// </summary>
internal sealed class RegionFrameSource : IDisposable
{
    private const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly object _gate = new();
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Windows.Graphics.SizeInt32 _monitorSize;
    private readonly Windows.Foundation.Rect _source;
    private bool _stopped;

    [SupportedOSPlatformGuard("windows10.0.19041.0")]
    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && GraphicsCaptureSession.IsSupported();

    public CanvasDevice Device { get; } = CanvasDevice.GetSharedDevice();

    /// <summary>The captured area in virtual-screen physical pixels (the requested region, clipped to its monitor).</summary>
    public Rectangle Region { get; }

    /// <summary>Raised on a capture thread for every new frame. Handlers must not keep the frame.</summary>
    public event Action<RegionFrame>? FrameArrived;

    /// <summary>Raised once (on any thread) when capture ends by itself: monitor removed or resolution changed.</summary>
    public event Action? Stopped;

    public RegionFrameSource(Rectangle region, bool includeCursor)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Live capture needs Windows 10 2004 or later.");
        var monitor = PInvoke.MonitorFromPoint(new Point(region.X + region.Width / 2, region.Y + region.Height / 2),
            MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var bounds = MonitorBounds(monitor);
        Region = Rectangle.Intersect(region, bounds);
        if (Region.Width < 1 || Region.Height < 1) throw new InvalidOperationException("The region is not on a monitor.");
        _source = new Windows.Foundation.Rect(Region.X - bounds.X, Region.Y - bounds.Y, Region.Width, Region.Height);

        _item = CaptureInterop.CreateForMonitor(monitor);
        _item.Closed += (_, _) => Stop();
        _monitorSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Device, Format, 2, _monitorSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = includeCursor;
        CaptureInterop.TryDisableBorder(_session);
    }

    public void Start() => _session.StartCapture();

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        bool lost = false;
        lock (_gate)
        {
            if (_stopped) return;
            try
            {
                using var frame = pool.TryGetNextFrame();
                if (frame is null) return;
                // The region was measured against the monitor as it was; a new resolution makes it meaningless.
                if (frame.ContentSize.Width != _monitorSize.Width || frame.ContentSize.Height != _monitorSize.Height)
                    lost = true;
                else
                {
                    using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(Device, frame.Surface);
                    FrameArrived?.Invoke(new RegionFrame(bitmap, _source, frame.SystemRelativeTime));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                lost = true; // e.g. GPU device removed
            }
        }
        if (lost) Stop();
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        Stopped?.Invoke();
    }

    private static unsafe Rectangle MonitorBounds(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("GetMonitorInfo failed.");
        var r = info.rcMonitor;
        return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true; // a frame being handled finishes first; later ones are dropped
            _session.Dispose();
            _pool.Dispose();
        }
    }
}

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
    private long _lastDelivered;  // Stopwatch slot of the last delivered frame (for MaxFps)
    private Timer? _throttle;     // collects a frame held back by MaxFps

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

    /// <summary>
    /// Most frames per second to deliver, read for every frame (so a change applies at once); 0 or less = all of them.
    /// </summary>
    public Func<int> MaxFps { get; init; } = () => 0;

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _) => Deliver(fromTimer: false);

    /// <summary>
    /// Hands the newest frame to <see cref="FrameArrived"/>. Under a frame rate limit a frame that comes too soon is
    /// left in the pool and a timer collects it when its turn comes: capture pauses while the pool is full, so frames
    /// over the limit cost almost nothing, and the last change before the screen goes still still gets through.
    /// </summary>
    private void Deliver(bool fromTimer)
    {
        bool lost = false;
        lock (_gate)
        {
            if (_stopped) return;
            try
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                int fps = MaxFps();
                long interval = fps > 0 ? System.Diagnostics.Stopwatch.Frequency / fps : 0;
                long due = _lastDelivered + interval;
                // A little early still counts: frames jitter and the timer only ticks every ~15 ms.
                if (!fromTimer && fps > 0 && now < due - interval / 4)
                {
                    long ms = Math.Max(1, (due - now) * 1000 / System.Diagnostics.Stopwatch.Frequency + 1);
                    (_throttle ??= new Timer(_ => Deliver(fromTimer: true))).Change(ms, Timeout.Infinite);
                    return;
                }

                // Only the newest of the frames waiting in the pool is shown.
                Direct3D11CaptureFrame? frame = null;
                while (_pool.TryGetNextFrame() is { } next)
                {
                    frame?.Dispose();
                    frame = next;
                }
                using var newest = frame;
                if (frame is null) return;
                // Keep to the slot the frame was due in, so early and late ones even out at the limit
                // (unless it is a whole interval late: the screen was still).
                _lastDelivered = fps > 0 && now - due < interval ? due : now;
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
            _throttle?.Dispose();
            _session.Dispose();
            _pool.Dispose();
        }
    }
}

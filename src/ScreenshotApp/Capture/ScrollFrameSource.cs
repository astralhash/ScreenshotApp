using System.Drawing;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Win32.Foundation;

namespace ScreenshotApp.Capture;

/// <summary>
/// A running Windows.Graphics.Capture session on one window for a scrolling capture. Keeps only the newest frame
/// (no copies while frames arrive) and reads the scroll area out of it on request. Windows in front of the target
/// are not in the frames; the cursor is not captured.
/// </summary>
internal sealed class ScrollFrameSource : IDisposable
{
    private readonly HWND _hwnd;
    private readonly Rectangle _area;
    private readonly CanvasDevice _device = CanvasDevice.GetSharedDevice();
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _lock = new();
    private Direct3D11CaptureFrame? _latest;
    private long _count, _lastTick;
    private Rectangle _crop; // the area inside the frame, fixed with the first read
    private bool _disposed;

    /// <summary>Frames received so far.</summary>
    public long FrameCount => Interlocked.Read(ref _count);

    /// <summary>Environment.TickCount64 of the newest frame.</summary>
    public long LastFrameTick => Interlocked.Read(ref _lastTick);

    /// <summary>The part of the area that is read (screen pixels); set by the first successful <see cref="Read"/>.</summary>
    public Rectangle Captured { get; private set; }

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && GraphicsCaptureSession.IsSupported();

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    public ScrollFrameSource(HWND hwnd, Rectangle area)
    {
        _hwnd = hwnd;
        _area = area;
        var item = CaptureInterop.CreateForWindow(hwnd);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        CaptureInterop.TryDisableBorder(_session);
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        lock (_lock)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }
            _latest?.Dispose();
            _latest = frame;
        }
        Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        Interlocked.Increment(ref _count);
    }

    /// <summary>
    /// The scroll area of the newest frame as top-down BGRA, or null when there is no frame yet or the window
    /// changed size (the area no longer lines up).
    /// </summary>
    public byte[]? Read()
    {
        lock (_lock)
        {
            if (_latest is null || _disposed) return null;
            int w = _latest.ContentSize.Width, h = _latest.ContentSize.Height;
            if (_crop.IsEmpty)
            {
                var origin = WindowCapture.FrameOrigin(_hwnd, w, h);
                var crop = Rectangle.Intersect(new Rectangle(_area.X - origin.X, _area.Y - origin.Y, _area.Width, _area.Height),
                    new Rectangle(0, 0, w, h));
                if (crop.Width < 1 || crop.Height < 1) return null;
                _crop = crop;
                Captured = new Rectangle(origin.X + crop.X, origin.Y + crop.Y, crop.Width, crop.Height);
            }
            if (_crop.Right > w || _crop.Bottom > h) return null;
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, _latest.Surface);
            return bitmap.GetPixelBytes(_crop.X, _crop.Y, _crop.Width, _crop.Height);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
    }
}

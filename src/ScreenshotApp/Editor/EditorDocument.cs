using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor.Model;
using ScreenshotApp.Services;

namespace ScreenshotApp.Editor;

/// <summary>One screenshot in the editor session: its pixels, save state, film-strip thumbnail and view state.</summary>
internal sealed class EditorDocument : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>True for the clip shown in the editor; drives the active marker under its thumbnail.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private const int ThumbWidth = 192, ThumbHeight = 108;

    // The capture is kept as compressed PNG bytes (about 2 MB for 4K instead of 33 MB of raw pixels).
    // Raw pixels are only held while the shot is being edited; see EnsureLoadedAsync / Unload.
    private CapturedImage? _pixels;
    private byte[]? _png;
    private readonly Task<byte[]>? _pngTask;

    public Guid Id { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>The raw pixels. Only valid while <see cref="IsLoaded"/>; call <see cref="EnsureLoadedAsync"/> first.</summary>
    public CapturedImage Image => _pixels ?? throw new InvalidOperationException("Clip pixels are not loaded.");

    public bool IsLoaded => _pixels is not null;

    /// <summary>The compressed capture; completes once the background encode (new clips) has finished.</summary>
    public Task<byte[]> GetPngAsync() => _png is { } png ? Task.FromResult(png) : _pngTask!;

    /// <summary>Decodes the pixels from the compressed copy if they were released.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_pixels is not null) return;
        var decoded = await ImageSaver.DecodeAsync(await GetPngAsync());
        _pixels ??= decoded;
    }

    /// <summary>Releases the raw pixels (only once a compressed copy exists to reload from).</summary>
    public void Unload()
    {
        if (_png is not null) _pixels = null;
    }

    /// <summary>Where this shot was last saved, if ever.</summary>
    public string? SavedPath { get; set; }

    /// <summary>True when the shot was edited after it was last saved.</summary>
    public bool Dirty { get; set; }

    public ImageSource Thumbnail { get; }
    public List<Annotation> Annotations { get; } = new();
    public CommandStack Commands { get; } = new();

    /// <summary>Non-destructive crop; the original pixels are always kept.</summary>
    public CropState Crop { get; } = new();

    /// <summary>Size of the exported image: the crop if there is one, else the full capture.</summary>
    public int OutputWidth => Crop.Rect is { } r ? (int)r.Width : Width;
    public int OutputHeight => Crop.Rect is { } r ? (int)r.Height : Height;

    // Per-shot view state, restored when switching back to this shot.
    public double Zoom { get; set; } = 1;
    public Vector2 Offset { get; set; }
    public bool FitMode { get; set; } = true;

    /// <param name="png">The already-compressed capture (restored clips); null for a new capture, which is encoded in the background.</param>
    public EditorDocument(CapturedImage image, Guid? id = null, byte[]? png = null)
    {
        Id = id ?? Guid.NewGuid();
        Width = image.Width;
        Height = image.Height;
        _pixels = image;
        _png = png;
        Thumbnail = MakeThumbnail(image);

        if (png is null)
        {
            // Off the UI thread so the encoder never waits on it; _png is published when done.
            _pngTask = Task.Run(async () =>
            {
                var bytes = await ImageSaver.EncodeAsync(image, jpg: false).ConfigureAwait(false);
                _png = bytes;
                return bytes;
            });
        }
    }

    /// <summary>Box-sampled downscale; must run on the UI thread (creates a WriteableBitmap).</summary>
    private static WriteableBitmap MakeThumbnail(CapturedImage img)
    {
        double k = Math.Min(Math.Min((double)ThumbWidth / img.Width, (double)ThumbHeight / img.Height), 1.0);
        int tw = Math.Max(1, (int)(img.Width * k)), th = Math.Max(1, (int)(img.Height * k));
        var buf = new byte[tw * th * 4];
        var src = img.Pixels;

        for (int y = 0; y < th; y++)
        {
            int sy0 = y * img.Height / th, sy1 = Math.Max(sy0 + 1, (y + 1) * img.Height / th);
            int ystep = Math.Max(1, (sy1 - sy0) / 4);
            for (int x = 0; x < tw; x++)
            {
                int sx0 = x * img.Width / tw, sx1 = Math.Max(sx0 + 1, (x + 1) * img.Width / tw);
                int xstep = Math.Max(1, (sx1 - sx0) / 4);
                int b = 0, g = 0, r = 0, n = 0;
                for (int sy = sy0; sy < sy1; sy += ystep)
                for (int sx = sx0; sx < sx1; sx += xstep)
                {
                    int i = (sy * img.Width + sx) * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; n++;
                }
                int o = (y * tw + x) * 4;
                buf[o] = (byte)(b / n); buf[o + 1] = (byte)(g / n); buf[o + 2] = (byte)(r / n); buf[o + 3] = 0xFF;
            }
        }

        var wb = new WriteableBitmap(tw, th);
        buf.AsBuffer().CopyTo(wb.PixelBuffer);
        wb.Invalidate();
        return wb;
    }
}

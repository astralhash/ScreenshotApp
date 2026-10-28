using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using ScreenshotApp.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ScreenshotApp.Services;

internal static partial class ImageSaver
{
    /// <summary>Expands yyyy, MM, dd, HH, mm, ss tokens; everything else is literal.</summary>
    public static string ExpandPattern(string pattern, DateTime now) =>
        TokenRegex().Replace(pattern, m => now.ToString(m.Value));

    [GeneratedRegex("yyyy|MM|dd|HH|mm|ss")]
    private static partial Regex TokenRegex();

    /// <summary>Saves to the configured folder using the filename pattern; returns the full path.</summary>
    public static async Task<string> SaveAsync(CapturedImage image, AppSettings settings)
    {
        bool jpg = settings.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
        string name = ExpandPattern(settings.FilenamePattern, DateTime.Now);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        Directory.CreateDirectory(settings.SaveFolder);
        string path = Path.Combine(settings.SaveFolder, name + (jpg ? ".jpg" : ".png"));
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(settings.SaveFolder, $"{name} ({i}){(jpg ? ".jpg" : ".png")}");

        await SaveAsync(image, path, jpg);
        return path;
    }

    public static async Task SaveAsync(CapturedImage image, string path, bool jpg) =>
        await File.WriteAllBytesAsync(path, await EncodeAsync(image, jpg));

    /// <summary>Decodes a PNG/JPG file to BGRA pixels (screen position is not meaningful and set to 0,0).</summary>
    public static async Task<CapturedImage> DecodeAsync(string path) =>
        await DecodeAsync(await File.ReadAllBytesAsync(path));

    public static async Task<CapturedImage> DecodeAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return new CapturedImage(data.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, 0, 0);
    }

    public static async Task<byte[]> EncodeAsync(CapturedImage image, bool jpg)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(
            jpg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }
}

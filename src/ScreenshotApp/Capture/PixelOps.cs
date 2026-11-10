using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ScreenshotApp.Capture;

/// <summary>
/// Whole-image BGRA loops that run between the hotkey and the overlay appearing; vectorized, because a 4K screen is
/// 33 MB to walk through.
/// </summary>
internal static unsafe class PixelOps
{
    /// <summary>Sets the alpha byte of every pixel to 255.</summary>
    public static void SetOpaque(Span<byte> bgra)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra);
        const uint alpha = 0xFF000000; // little-endian: the fourth byte
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var mask = new Vector<uint>(alpha);
            for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                (new Vector<uint>(pixels[i..]) | mask).CopyTo(pixels[i..]);
        }
        for (; i < pixels.Length; i++) pixels[i] |= alpha;
    }

    /// <summary>
    /// Writes <paramref name="src"/> at about 39% brightness (x * 100 >> 8), opaque, to <paramref name="dst"/>.
    /// <paramref name="byteCount"/> is a multiple of 4.
    /// </summary>
    public static void Dim(byte* src, byte* dst, int byteCount)
    {
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var factor = Vector128.Create((ushort)100);
            var alpha = Vector128.Create(0xFF000000u).AsByte();
            for (; i <= byteCount - 16; i += 16)
            {
                var (lower, upper) = Vector128.Widen(Vector128.Load(src + i));
                var dimmed = Vector128.Narrow(
                    Vector128.ShiftRightLogical(lower * factor, 8), Vector128.ShiftRightLogical(upper * factor, 8));
                (dimmed | alpha).Store(dst + i);
            }
        }
        for (; i < byteCount; i += 4)
        {
            dst[i] = (byte)(src[i] * 100 >> 8);
            dst[i + 1] = (byte)(src[i + 1] * 100 >> 8);
            dst[i + 2] = (byte)(src[i + 2] * 100 >> 8);
            dst[i + 3] = 0xFF;
        }
    }
}

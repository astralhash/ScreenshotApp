#if DEBUG
using System.Drawing;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor.Model;
using ScreenshotApp.Services;
using Color = Windows.UI.Color;

namespace ScreenshotApp.Editor;

/// <summary>
/// Debug-only check of the redaction guarantees, run with <c>ScreenshotApp.exe --selftest-redaction</c>. It renders a
/// noise image (every pixel random, so any surviving original pixel is detectable) through the real export path
/// and writes PASS/FAIL lines to log.txt. Not compiled into release builds.
/// </summary>
internal static class RedactionSelfTest
{
    public static bool Run()
    {
        bool allOk = true;
        void Check(string name, bool pass, string detail = "")
        {
            allOk &= pass;
            Log.Info($"SELFTEST {(pass ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        const int W = 400, H = 240;
        var rng = new Random(42);
        var pixels = new byte[W * H * 4];
        rng.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        var source = new CapturedImage(pixels, W, H, 0, 0);

        foreach (var mode in new[] { BlurMode.Pixelate, BlurMode.Blur, BlurMode.Solid })
        {
            // Fractional corners on purpose: the area must be snapped outwards to whole pixels.
            var redaction = new BlurAnnotation
            {
                A = new(50.4f, 40.7f),
                B = new(250.6f, 110.2f),
                Mode = mode,
                StrokeWidth = 1, // below every floor: the minimum strength must still apply
                Color = Color.FromArgb(255, 255, 0, 255),
            };
            var area = redaction.SnappedBox;
            int left = (int)area.Left, top = (int)area.Top, right = (int)area.Right, bottom = (int)area.Bottom;

            var result = ImageExporter.Render(source, new[] { redaction });

            int outsideChanged = 0, insideTotal = 0, ringTotal = 0, ringUnchanged = 0, insideUnchanged = 0;
            var distinctRed = new HashSet<byte>();
            bool allSolid = true;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool same = result.Pixels[i] == pixels[i] && result.Pixels[i + 1] == pixels[i + 1] && result.Pixels[i + 2] == pixels[i + 2];
                bool inside = x >= left && x < right && y >= top && y < bottom;
                if (!inside)
                {
                    if (!same) outsideChanged++;
                    continue;
                }
                insideTotal++;
                if (same) insideUnchanged++;
                distinctRed.Add(result.Pixels[i + 2]);
                if (x == left || x == right - 1 || y == top || y == bottom - 1)
                {
                    ringTotal++;
                    if (same) ringUnchanged++;
                }
                if (!(result.Pixels[i] == 255 && result.Pixels[i + 1] == 0 && result.Pixels[i + 2] == 255)) allSolid = false;
            }

            Check($"{mode}: nothing outside the snapped box changed", outsideChanged == 0, $"changed={outsideChanged}");
            // Pure noise: an original pixel survives by chance in a few percent of cases at most.
            double ringKept = ringTotal == 0 ? 1 : (double)ringUnchanged / ringTotal;
            Check($"{mode}: outermost ring is redacted too (no sliver of the original)", ringKept < 0.15,
                  $"{ringUnchanged}/{ringTotal} ring pixels equal the original");

            if (mode == BlurMode.Solid)
            {
                Check("Solid: every pixel in the box is the fill colour", allSolid, $"unchanged-from-original={insideUnchanged}");
            }
            else
            {
                int levels = mode == BlurMode.Pixelate ? 8 : 16;
                Check($"{mode}: result is quantized to at most {levels} levels per channel", distinctRed.Count <= levels,
                      $"distinct red values={distinctRed.Count}");
                double kept = (double)insideUnchanged / insideTotal;
                Check($"{mode}: original pixels do not survive inside the box", kept < 0.10, $"{insideUnchanged}/{insideTotal} equal");
            }
        }

        // Noise averages out to flat grey, so also use a smooth horizontal gradient: without quantization the result
        // would contain dozens of distinct levels; with it, at most 8 (Pixelate) or 16 (Blur).
        var gradient = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            byte v = (byte)(x * 255 / (W - 1));
            gradient[i] = gradient[i + 1] = gradient[i + 2] = v;
            gradient[i + 3] = 0xFF;
        }
        foreach (var (mode, levels) in new[] { (BlurMode.Pixelate, 8), (BlurMode.Blur, 16) })
        {
            var redaction = new BlurAnnotation { A = new(20, 20), B = new(380, 120), Mode = mode, StrokeWidth = 6 };
            var result = ImageExporter.Render(new CapturedImage(gradient, W, H, 0, 0), new[] { redaction });
            var levelsSeen = new HashSet<byte>();
            for (int y = 20; y < 120; y++)
            for (int x = 20; x < 380; x++) levelsSeen.Add(result.Pixels[(y * W + x) * 4 + 2]);
            Check($"{mode}: gradient comes out in at most {levels} levels (quantizer is active)",
                  levelsSeen.Count <= levels && levelsSeen.Count >= 3, $"distinct levels={levelsSeen.Count}");
        }

        // Crop is part of the permanent edit: the output must be exactly the crop and contain nothing else.
        var crop = new RectangleF(20, 30, 100, 60);
        var cropped = ImageExporter.Render(source, Array.Empty<Annotation>(), crop);
        bool sizeOk = cropped.Width == 100 && cropped.Height == 60;
        bool contentOk = true;
        for (int y = 0; y < 60 && contentOk; y++)
        for (int x = 0; x < 100; x++)
        {
            int o = (y * 100 + x) * 4, s = ((y + 30) * W + x + 20) * 4;
            if (cropped.Pixels[o] != pixels[s] || cropped.Pixels[o + 1] != pixels[s + 1] || cropped.Pixels[o + 2] != pixels[s + 2]) { contentOk = false; break; }
        }
        Check("Crop: export is exactly the cropped pixels", sizeOk && contentOk, $"size={cropped.Width}x{cropped.Height}");

        Log.Info($"SELFTEST {(allOk ? "ALL PASSED" : "SOME CHECKS FAILED")}");
        return allOk;
    }
}
#endif

#if DEBUG
using ScreenshotApp.Services;

namespace ScreenshotApp.Capture;

/// <summary>
/// Debug-only check of <see cref="ScrollStitcher"/>, run with <c>ScreenshotApp.exe --selftest-scroll</c>. Cuts a tall
/// synthetic page (text-like lines, blank stretches, a sticky header and footer, a moving scrollbar thumb) into
/// overlapping frames at uneven scroll steps and checks the stitched result is pixel-identical to the page.
/// Writes PASS/FAIL lines to log.txt.
/// </summary>
internal static class ScrollSelfTest
{
    private const int W = 640, Scrollbar = 16;

    public static bool Run()
    {
        bool allOk = true;
        void Check(string name, bool pass, string detail = "")
        {
            allOk &= pass;
            Log.Info($"SELFTEST {(pass ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        var rng = new Random(7);
        var page = MakePage(rng, 5000);

        foreach (var (name, header, footer, view, steps, scrollbar) in new (string, int, int, int, int[], bool)[]
        {
            ("header+footer, uneven steps", 60, 40, 700, [300, 417, 523, 250, 333, 401], true),
            ("no header/footer, fixed step", 0, 0, 600, [360], true),
            ("tall header, small steps", 150, 0, 500, [97, 120, 180], true),
            ("no scrollbar (edge content kept)", 60, 40, 700, [300, 360], false),
        })
        {
            var stitcher = new ScrollStitcher(W, view, 100_000, Scrollbar);
            int contentView = view - header - footer, pos = 0, i = 0;
            var results = new List<StitchResult>();
            while (true)
            {
                results.Add(stitcher.Add(Frame(page, pos, header, footer, view, scrollbar)));
                if (pos == page.Rows - contentView) break;
                pos = Math.Min(page.Rows - contentView, pos + steps[i++ % steps.Length]);
            }
            // At the bottom, one more frame of the same position must say "nothing moved".
            var last = stitcher.Add(Frame(page, pos, header, footer, view, scrollbar));
            var image = stitcher.Build(0, 0);
            int expectedWidth = scrollbar ? W - Scrollbar : W;
            Check($"{name}: width (scrollbar {(scrollbar ? "cut" : "kept")})", image.Width == expectedWidth, $"{image.Width} vs {expectedWidth}");

            int expectedHeight = header + page.Rows + footer;
            bool allAdded = results.All(r => r == StitchResult.Added);
            Check($"{name}: every step matched", allAdded, string.Join(",", results));
            Check($"{name}: end detected", last == StitchResult.NoChange, last.ToString());
            Check($"{name}: height", image.Height == expectedHeight, $"{image.Height} vs {expectedHeight}");
            if (image.Height == expectedHeight && image.Width == expectedWidth)
            {
                int bad = CountDifferentRows(image, page, header, footer);
                Check($"{name}: pixels identical", bad == 0, $"{bad} rows differ");
            }
        }

        // Height limit.
        {
            var stitcher = new ScrollStitcher(W, 600, 2000, Scrollbar);
            StitchResult r = StitchResult.Added;
            for (int pos = 0; pos <= 4000 && r != StitchResult.Full; pos += 400)
                r = stitcher.Add(Frame(page, pos, 0, 0, 600, true));
            Check("height limit", r == StitchResult.Full && stitcher.Height == 2000, $"{r}, {stitcher.Height}");
        }

        // A frame that does not belong to the page must not be stitched in.
        {
            var stitcher = new ScrollStitcher(W, 600, 100_000, Scrollbar);
            stitcher.Add(Frame(page, 0, 0, 0, 600, true));
            var other = MakePage(new Random(99), 600);
            var r = stitcher.Add(Frame(other, 0, 0, 0, 600, true));
            Check("unrelated frame rejected", r == StitchResult.NoMatch, r.ToString());
        }

        Log.Info($"SELFTEST scroll {(allOk ? "ALL PASS" : "FAILED")}");
        return allOk;
    }

    /// <summary>
    /// <c>--selftest-scroll-live</c>: opens a window of this app with a long ScrollViewer, finds it with UI Automation,
    /// runs a real scrolling capture on it (no wheel input) and checks the height against the content's extent.
    /// The result is saved to %TEMP%\scroll-selftest.png.
    /// </summary>
    public static async Task<bool> RunLiveAsync()
    {
        bool allOk = true;
        void Check(string name, bool pass, string detail = "")
        {
            allOk &= pass;
            Log.Info($"SELFTEST {(pass ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        var rng = new Random(3);
        var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Padding = new Microsoft.UI.Xaml.Thickness(12) };
        string[] words = ["alpha", "beta", "gamma", "delta", "scroll", "capture", "window", "stitch", "frame", "pixel", "row"];
        for (int i = 0; i < 160; i++)
        {
            if (i % 12 == 5)
            {
                panel.Children.Add(new Microsoft.UI.Xaml.Controls.Border
                {
                    Height = 40 + rng.Next(60),
                    Margin = new Microsoft.UI.Xaml.Thickness(0, 4, rng.Next(200), 4),
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255,
                        (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))),
                });
                continue;
            }
            var text = $"Row {i:000}  " + string.Join(' ', Enumerable.Range(0, 3 + rng.Next(8)).Select(_ => words[rng.Next(words.Length)]));
            panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock { Text = text, FontSize = 15, Margin = new Microsoft.UI.Xaml.Thickness(0, 2, 0, 2) });
        }
        var scroller = new Microsoft.UI.Xaml.Controls.ScrollViewer { Content = panel };
        var window = new Microsoft.UI.Xaml.Window { Title = "Scroll self-test", Content = scroller };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(700, 600));
        window.Activate();
        await Task.Delay(1500);

        try
        {
            var hwnd = new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
            var frame = WindowEnumerator.GetFrameBounds(hwnd) ?? default;
            var centre = new System.Drawing.Point(frame.X + frame.Width / 2, frame.Y + frame.Height / 2);
            using var target = await Task.Run(() => ScrollAutomation.Find(hwnd, centre));
            Check("UI Automation finds the ScrollViewer", target.HasElement, $"area {target.Area}, frame {frame}");

            var image = await ScrollCapture.RunAsync(target, allowWheel: false);
            Check("capture produced an image", image is not null);
            if (image is null) return false;

            double scale = scroller.XamlRoot.RasterizationScale;
            // The area may stop short of the viewport's bottom (window corners); those last rows are never visible.
            int hidden = (int)Math.Round(scroller.ViewportHeight * scale) - target.Area.Height;
            int expected = (int)Math.Round(scroller.ExtentHeight * scale) - Math.Max(0, hidden);
            Check("scrolled with UI Automation", ScrollCapture.LastMethod == ScrollMethod.Automation, ScrollCapture.LastMethod.ToString());
            Check("height matches the content extent", Math.Abs(image.Height - expected) <= 3 + scale * 2,
                $"{image.Height} px vs extent {expected} px (viewport {target.Area.Height} px)");

            var path = Path.Combine(Path.GetTempPath(), "scroll-selftest.png");
            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            using var bitmap = Microsoft.Graphics.Canvas.CanvasBitmap.CreateFromBytes(device, image.Pixels, image.Width, image.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            await bitmap.SaveAsync(path);
            Log.Info($"SELFTEST scroll-live image: {path}");
        }
        finally
        {
            window.Close();
        }
        Log.Info($"SELFTEST scroll-live {(allOk ? "ALL PASS" : "FAILED")}");
        return allOk;
    }

    private sealed record Page(byte[] Pixels, int Rows, byte[] Header, byte[] Footer);

    /// <summary>White page with dark "words" in 18 px lines, blank stretches and a few photo-like noisy blocks.</summary>
    private static Page MakePage(Random rng, int rows)
    {
        var px = new byte[W * rows * 4];
        Array.Fill(px, (byte)0xFF);
        int y = 80; // starts with blank space, like a page's top margin
        bool lastBlank = true;
        while (y < rows - 20)
        {
            int kind = rng.Next(10);
            if (kind == 0 && lastBlank) kind = 2;
            lastBlank = kind == 0;
            if (kind == 0) { y += rng.Next(40, 140); continue; } // blank stretch (never two in a row)
            if (kind == 1)
            {
                int h = Math.Min(rng.Next(60, 200), rows - y);
                for (int yy = y; yy < y + h; yy++)
                for (int x = 40; x < W - 80; x++)
                    SetPixel(px, x, yy, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                y += h + 10;
                continue;
            }
            int xw = 20;
            while (xw < W - 60)
            {
                int ww = rng.Next(12, 70);
                byte shade = (byte)rng.Next(0, 90);
                for (int yy = y + 3; yy < y + 14; yy++)
                for (int x = xw; x < Math.Min(xw + ww, W - 40); x++)
                    if (rng.Next(3) != 0) SetPixel(px, x, yy, shade, shade, shade);
                xw += ww + rng.Next(5, 12);
            }
            y += 18;
        }
        return new Page(px, rows, Band(rng, 150, 0x30), Band(rng, 40, 0x50));
    }

    private static byte[] Band(Random rng, int rows, byte shade)
    {
        var px = new byte[W * rows * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = shade; px[i + 1] = shade; px[i + 2] = (byte)(shade + 40); px[i + 3] = 255; }
        for (int k = 0; k < 20; k++)
            SetPixel(px, rng.Next(W), rng.Next(rows), 255, 255, 255);
        return px;
    }

    private static void SetPixel(byte[] px, int x, int y, byte b, byte g, byte r)
    {
        int i = (y * W + x) * 4;
        px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
    }

    /// <summary>The view at scroll position <paramref name="pos"/>: header, page rows, footer, and a scrollbar thumb.</summary>
    private static byte[] Frame(Page page, int pos, int header, int footer, int view, bool scrollbar)
    {
        int stride = W * 4, content = view - header - footer;
        var f = new byte[stride * view];
        Buffer.BlockCopy(page.Header, 0, f, 0, header * stride);
        Buffer.BlockCopy(page.Pixels, pos * stride, f, header * stride, content * stride);
        Buffer.BlockCopy(page.Footer, 0, f, (view - footer) * stride, footer * stride);
        if (!scrollbar) return f;
        // Scrollbar: grey track, dark thumb at a position following the scroll position.
        int thumbTop = (int)((long)pos * view / page.Rows), thumbH = Math.Max(20, view * content / page.Rows);
        for (int y = 0; y < view; y++)
        {
            byte c = y >= thumbTop && y < thumbTop + thumbH ? (byte)0x60 : (byte)0xE0;
            for (int x = W - Scrollbar; x < W; x++)
            {
                int i = y * stride + x * 4;
                f[i] = c; f[i + 1] = c; f[i + 2] = c; f[i + 3] = 255;
            }
        }
        return f;
    }

    /// <summary>Rows of the result that differ from header + page + footer (all of the result's columns).</summary>
    private static int CountDifferentRows(CapturedImage image, Page page, int header, int footer)
    {
        int stride = W * 4, bytes = image.Width * 4, bad = 0;
        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> expected = y < header ? page.Header.AsSpan(y * stride, bytes)
                : y < header + page.Rows ? page.Pixels.AsSpan((y - header) * stride, bytes)
                : page.Footer.AsSpan((y - header - page.Rows) * stride, bytes);
            if (!image.Pixels.AsSpan(y * bytes, bytes).SequenceEqual(expected)) bad++;
        }
        return bad;
    }
}
#endif

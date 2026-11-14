using System.Drawing;
using Microsoft.Graphics.Canvas;
using ScreenshotApp.Services;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenshotApp.Capture;

/// <summary>
/// Runs a scrolling capture of a picked area: scrolls it down step by step (UI Automation, then WM_VSCROLL, then
/// the mouse wheel, whichever moves it), grabs the window after each step once it has settled, and stitches the
/// frames. Ends at the bottom, at the height limit, on Esc / Stop, or (wheel only) when the user moves the mouse.
/// Scrolling the area by hand meanwhile works too: the stitcher only looks at the frames.
/// </summary>
internal static class ScrollCapture
{
    private const double TargetStep = 0.5;   // aim to scroll half the area per step
    private const int NewFrameTimeoutMs = 700, QuietMs = 150, SettleTimeoutMs = 2000, PollMs = 25;
    private const int MaxSteps = 2000;
    private const double MaxWheelNotches = 10, MaxLines = 60; // a misread step must not snowball

    public static bool IsActive { get; private set; }

    /// <summary>The scroll method the last capture ended with (for the log and the self-test).</summary>
    public static ScrollMethod LastMethod { get; private set; }

    /// <summary>The stitched image, or null when the window could not be captured at all.</summary>
    /// <param name="allowWheel">False to never inject wheel input (self-test).</param>
    public static async Task<CapturedImage?> RunAsync(ScrollTarget target, bool allowWheel = true)
    {
        if (IsActive || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !ScrollFrameSource.IsSupported) return null;
        IsActive = true;
        ScrollAutomation.ResetWheel();
        RegionOutline? outline = null;
        ScrollToolbar? toolbar = null;
        ScrollFrameSource? source = null;
        bool stop = false;
        // Esc is latched by a fast background poll: the loop below spends most of its time scrolling, settling
        // and stitching, and a short tap checked only now and then was missed (the capture went on for seconds).
        int escPressed = 0;
        using var escWatch = new System.Threading.Timer(_ => { if (EscDown()) Volatile.Write(ref escPressed, 1); }, null, 0, 15);
        try
        {
            await BringToFrontAsync(target);
            outline = new RegionOutline(target.Area);
            outline.Show(true);
            toolbar = new ScrollToolbar(target.Area);
            toolbar.StopRequested += () => stop = true;

            source = new ScrollFrameSource(target.Window, target.Area);
            bool Stopping() => stop || Volatile.Read(ref escPressed) != 0 || WheelCursorMoved();

            byte[]? first = await NextFrameAsync(source, 0, Stopping);
            if (first is null) return null;
            var captured = source.Captured;
            int maxHeight = Math.Max(captured.Height, CanvasDevice.GetSharedDevice().MaximumBitmapSizeInPixels);
            var stitcher = new ScrollStitcher(captured.Width, captured.Height, maxHeight, ScrollbarWidth(target));
            stitcher.Add(first);

            var methods = new List<ScrollMethod>();
            if (target.HasElement) methods.Add(ScrollMethod.Automation);
            if (target.ScrollWindow != default) methods.Add(ScrollMethod.ScrollMessage);
            if (allowWheel || methods.Count == 0) methods.Add(ScrollMethod.Wheel);
            var amounts = new Dictionary<ScrollMethod, double>
            {
                [ScrollMethod.Automation] = TargetStep, // fraction of the view
                [ScrollMethod.ScrollMessage] = 5,       // lines
                [ScrollMethod.Wheel] = 2,               // notches
            };
            // Largest scroll per unit (notch, line, view fraction) seen so far: sizing the step from it never
            // overshoots, even when an app scrolls unevenly (Firefox: 180 to 360 px per notch).
            var perUnit = new Dictionary<ScrollMethod, double>();
            int mi = 0, still = 0, retries = 0;
            bool working = false; // the current method has moved the area at least once
            string reason = "end reached";
            Log.Info($"Scroll capture: area {captured}, UIA element {target.HasElement}, scroll window {target.ScrollWindow != default}, methods {string.Join("/", methods)}.");
            await DumpAsync(first, captured, 0);

            for (int step = 0; step < MaxSteps; step++)
            {
                if (Stopping()) { reason = "stopped"; break; }
                var method = methods[mi];
                long before = source.FrameCount;
                bool scrolled = await Task.Run(() => ScrollAutomation.Step(target, method, amounts[method]));
                if (!scrolled)
                {
                    if (working || ++mi >= methods.Count) break; // nothing left to scroll, or no method works
                    continue;
                }

                var frame = await NextFrameAsync(source, before, Stopping);
                if (frame is null)
                {
                    if (Stopping()) { reason = "stopped"; break; }
                    reason = "window changed size";
                    break;
                }
                if (Stopping()) { reason = "stopped"; break; } // pressed while it settled: skip the stitch
                var result = await Task.Run(() => stitcher.Add(frame));
                await DumpAsync(frame, captured, step + 1);
                Log.Info($"Scroll capture step {step + 1}: {method} x{amounts[method]:0.##} → {result} ({stitcher.LastInfo})");
                switch (result)
                {
                    case StitchResult.Added:
                    {
                        working = true;
                        still = retries = 0;
                        // Size the next step to scroll about half the area at the largest rate seen so far.
                        double rate = stitcher.LastOffset / ScrollAutomation.Units(method, amounts[method]);
                        perUnit[method] = Math.Max(rate, perUnit.GetValueOrDefault(method));
                        double wanted = TargetStep * captured.Height / perUnit[method];
                        amounts[method] = method switch
                        {
                            ScrollMethod.Automation => Math.Clamp(wanted, 0.1, 0.8),
                            ScrollMethod.Wheel => Math.Clamp(Math.Floor(wanted), 1, MaxWheelNotches),
                            _ => Math.Clamp(Math.Floor(wanted), 1, MaxLines),
                        };
                        toolbar.SetText($"Scrolling capture… {stitcher.Height:N0} px  ·  Esc to stop");
                        break;
                    }
                    case StitchResult.Full:
                        reason = "height limit";
                        step = MaxSteps;
                        break;
                    case StitchResult.NoChange:
                        if (!working)
                        {
                            if (++mi >= methods.Count) step = MaxSteps;
                        }
                        else if (++still >= 2) step = MaxSteps;
                        break;
                    case StitchResult.NoMatch:
                    {
                        // Probably scrolled too far: go back by the same amount and retry with half the step.
                        double smaller = ScrollAutomation.Units(method, amounts[method]) / 2;
                        bool canShrink = method == ScrollMethod.Automation ? smaller >= 0.1 : smaller >= 1;
                        if (retries < 3 && canShrink)
                        {
                            retries++;
                            long beforeBack = source.FrameCount;
                            await Task.Run(() => ScrollAutomation.Step(target, method, amounts[method], up: true));
                            await NextFrameAsync(source, beforeBack, Stopping);
                            amounts[method] = method == ScrollMethod.Automation ? smaller : Math.Floor(smaller);
                            Log.Info($"Scroll capture: frames did not line up; scrolled back, retrying with x{amounts[method]:0.##}.");
                            break;
                        }
                        reason = "frames did not line up";
                        step = MaxSteps;
                        break;
                    }
                }
            }

            LastMethod = methods[Math.Min(mi, methods.Count - 1)];
            Log.Info($"Scroll capture: {stitcher.Height} px, {reason}, method {LastMethod}.");
            return stitcher.Build(captured.X, captured.Y);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
        finally
        {
            source?.Dispose();
            toolbar?.Dispose();
            outline?.Dispose();
            ScrollAutomation.ResetWheel();
            IsActive = false;
        }
    }

    /// <summary>
    /// Waits for a frame newer than <paramref name="countBefore"/> (up to a short timeout: the area may not have
    /// moved), then for the frames to stop changing (smooth scrolling), and returns the area's pixels.
    /// </summary>
    private static async Task<byte[]?> NextFrameAsync(ScrollFrameSource source, long countBefore, Func<bool> stopping)
    {
        long start = Environment.TickCount64;
        while (source.FrameCount == countBefore && Environment.TickCount64 - start < NewFrameTimeoutMs)
        {
            if (stopping()) return null;
            await Task.Delay(PollMs);
        }
        while (Environment.TickCount64 - source.LastFrameTick < QuietMs && Environment.TickCount64 - start < SettleTimeoutMs)
        {
            if (stopping()) return null;
            await Task.Delay(PollMs);
        }
        if (countBefore == 0 && source.FrameCount == 0)
        {
            // The first frame can take a moment; give it up to a second more.
            while (source.FrameCount == 0 && Environment.TickCount64 - start < 1700) await Task.Delay(PollMs);
        }
        return await Task.Run(source.Read);
    }

    /// <summary>Debug: a folder to save every frame to (frame-000.png, …); null for none.</summary>
    public static string? DumpFolder { get; set; }

    private static async Task DumpAsync(byte[] frame, Rectangle size, int index)
    {
        if (DumpFolder is null) return;
        try
        {
            Directory.CreateDirectory(DumpFolder);
            var copy = (byte[])frame.Clone();
            for (int i = 3; i < copy.Length; i += 4) copy[i] = 0xFF;
            using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), copy, size.Width, size.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            await bitmap.SaveAsync(Path.Combine(DumpFolder, $"frame-{index:000}.png"));
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>
    /// Activates the target window first: partly covered windows often do not scroll or repaint their hidden part
    /// (browsers throttle occluded content), and the wheel fallback needs the area uncovered. This app owns the
    /// foreground right after the overlay click, so SetForegroundWindow is allowed.
    /// </summary>
    private static async Task BringToFrontAsync(ScrollTarget target)
    {
        if (PInvoke.GetForegroundWindow() == target.Window) return;
        if (!PInvoke.SetForegroundWindow(target.Window))
        {
            Log.Info("Scroll capture: could not bring the window to the front.");
            return;
        }
        await Task.Delay(250); // let it come up and repaint what was covered
    }

    private static bool EscDown() => (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_ESCAPE) & 0x8000) != 0;

    /// <summary>True when the wheel method placed the cursor and the user has moved it since.</summary>
    private static bool WheelCursorMoved()
    {
        if (ScrollAutomation.WheelCursor is not { } placed) return false;
        PInvoke.GetCursorPos(out var pt);
        return pt.X != placed.X || pt.Y != placed.Y;
    }

    /// <summary>Width of a classic vertical scrollbar at the window's DPI, left out of the frame comparison.</summary>
    private static int ScrollbarWidth(ScrollTarget target)
    {
        uint dpi = PInvoke.GetDpiForWindow(target.Window);
        int w = PInvoke.GetSystemMetricsForDpi(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXVSCROLL, dpi == 0 ? 96 : dpi);
        return w + (int)Math.Ceiling(4 * (dpi == 0 ? 1 : dpi / 96.0));
    }
}

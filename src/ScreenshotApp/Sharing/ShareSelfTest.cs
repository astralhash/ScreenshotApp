#if DEBUG
using System.Drawing;
using ScreenshotApp.Capture;
using ScreenshotApp.Services;
using Windows.Win32;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Sharing;

/// <summary>
/// Debug-only check of the hidden mirror window, run with <c>ScreenshotApp.exe --selftest-share</c>: it must pass
/// the filters screen-share pickers apply, lie wholly off-screen, and still deliver the region through window capture
/// (what Teams uses). Writes PASS/FAIL lines to log.txt. Not compiled into release builds.
/// </summary>
internal static class ShareSelfTest
{
    public static async Task<bool> RunAsync()
    {
        bool allOk = true;
        void Check(string name, bool pass, string detail = "")
        {
            allOk &= pass;
            Log.Info($"SELFTEST {(pass ? "PASS" : "FAIL")}  {name}  {detail}");
        }

        var screen = ScreenCapture.GetMonitorUnderCursor();
        var region = new Rectangle(screen.X + 40, screen.Y + 40, 480, 270);
        using var share = new RegionShare(region);
        await Task.Delay(1500); // first frames, window shown and sized
        var hwnd = share.Mirror.Handle;

        Check("visible, not minimized", PInvoke.IsWindowVisible(hwnd) && !PInvoke.IsIconic(hwnd));
        Check("unowned", PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull);
        uint exStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        Check("not a tool window", (exStyle & (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) == 0);
        Check("not cloaked", Cloaked(hwnd) == 0);
        PInvoke.GetWindowRect(hwnd, out var wr);
        var bounds = Rectangle.FromLTRB(wr.left, wr.top, wr.right, wr.bottom);
        Check("off-screen", !bounds.IntersectsWith(Monitors.VirtualScreen), bounds.ToString());
        var frame = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? await WindowCapture.GrabAsync(hwnd) : null;
        Check("window capture delivers a frame", frame is not null);
        if (frame is { } f)
        {
            Check("frame is the region size", f.Width == region.Width && f.Height == region.Height, $"{f.Width}x{f.Height}");
            if (f.Width == region.Width && f.Height == region.Height)
            {
                // The screen may change a little in between (cursor, caret), so most rather than all pixels.
                var expected = ScreenCapture.CaptureRect(region).Pixels;
                int same = 0, total = f.Width * f.Height;
                for (int i = 0; i < expected.Length; i += 4)
                    if (expected[i] == f.Pixels[i] && expected[i + 1] == f.Pixels[i + 1] && expected[i + 2] == f.Pixels[i + 2]) same++;
                Check("frame shows the region 1:1", same > total * 0.9, $"{same * 100.0 / total:F1}% identical");
            }
        }

        Log.Info(allOk ? "SELFTEST ALL PASSED" : "SELFTEST FAILURES");
        return allOk;
    }

    private static unsafe int Cloaked(Windows.Win32.Foundation.HWND hwnd)
    {
        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return cloaked;
    }
}
#endif

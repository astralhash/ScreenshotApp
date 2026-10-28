using System.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenshotApp.Capture;
using ScreenshotApp.Services;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace ScreenshotApp.Sharing;

/// <summary>
/// A window that shows a live screen region 1:1, for apps (Teams) that can share a window but not a region.
/// The user never sees it: it is parked beyond the edge of the virtual screen and has no taskbar button. It stays an
/// ordinary visible top-level window (unowned, not a tool window, not minimized or cloaked, with a title), because
/// screen-share pickers leave out anything else, and window capture still gets its content off-screen.
/// </summary>
internal sealed class MirrorWindow : Window
{
    public const string WindowTitle = "Shared region";
    private const int OffScreenGap = 64; // between the virtual screen and the window, in physical pixels

    private readonly CanvasDevice _device;
    private readonly CanvasSwapChainPanel _panel = new();
    private readonly object _gate = new(); // swap chain: drawn on the capture thread, resized on the UI thread
    private CanvasSwapChain? _swapChain;
    private bool _closing;

    public HWND Handle { get; }

    /// <summary>The window was closed from outside (Alt+Tab to it, Alt+F4); the share should end.</summary>
    public event Action? StopRequested;

    public MirrorWindow(CanvasDevice device)
    {
        _device = device;
        Title = WindowTitle;
        Handle = new HWND(WindowNative.GetWindowHandle(this));
        AppIcon.Apply(AppWindow); // shown next to the title in share pickers
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false; // a minimized window gives window capture nothing to show
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }
        Content = new Grid { Background = new SolidColorBrush(Colors.Black), Children = { _panel } };

        _panel.SizeChanged += (_, _) => UpdateSwapChain();
        _panel.CompositionScaleChanged += (_, _) => UpdateSwapChain();
        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; // the owner closes the window, after this event has returned
            DispatcherQueue.TryEnqueue(() => StopRequested?.Invoke());
        };
    }

    public void ShowFor(Rectangle region)
    {
        SetRegion(region);
        AppWindow.Show(activateWindow: false);
        RemoveTaskbarButton();
    }

    /// <summary>Client area = region size, the whole window just left of the virtual screen.</summary>
    public void SetRegion(Rectangle region)
    {
        RemoveFrame();
        // Off-screen first, so a DPI change from moving cannot rescale the size set next.
        MoveOffScreen();
        var want = new SizeInt32(region.Width, region.Height);
        AppWindow.ResizeClient(want);
        // ResizeClient may still allow for a frame this window does not have: correct by the real client size.
        if (PInvoke.GetClientRect(Handle, out var client) && (client.right != want.Width || client.bottom != want.Height))
        {
            var outer = AppWindow.Size;
            AppWindow.Resize(new SizeInt32(outer.Width + want.Width - client.right, outer.Height + want.Height - client.bottom));
        }
        MoveOffScreen(); // again for the new width
    }

    public void CloseWindow()
    {
        _closing = true;
        lock (_gate)
        {
            _swapChain?.Dispose();
            _swapChain = null;
        }
        Close();
    }

    /// <summary>Draws one frame; called on the capture thread.</summary>
    public void Draw(RegionFrame frame)
    {
        lock (_gate)
        {
            if (_swapChain is null) return;
            var size = _swapChain.Size; // DIPs
            var src = frame.Source;     // bitmap pixels (= DIPs at 96 dpi)
            double scale = Math.Min(size.Width / src.Width, size.Height / src.Height);
            double w = src.Width * scale, h = src.Height * scale;
            var dest = new Windows.Foundation.Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
            // 1:1 (the normal case) stays pixel-exact; anything else is scaled smoothly.
            bool exact = Math.Abs(scale * _swapChain.Dpi / 96 - 1) < 0.001;

            using (var ds = _swapChain.CreateDrawingSession(Colors.Black))
                ds.DrawImage(frame.Bitmap, dest, src, 1f,
                    exact ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear);
            _swapChain.Present();
        }
    }

    private void UpdateSwapChain()
    {
        float w = (float)_panel.ActualWidth, h = (float)_panel.ActualHeight;
        if (w < 1 || h < 1 || _closing) return;
        float dpi = 96f * _panel.CompositionScaleX;
        lock (_gate)
        {
            if (_swapChain is null)
            {
                _swapChain = new CanvasSwapChain(_device, w, h, dpi);
                _panel.SwapChain = _swapChain;
            }
            else _swapChain.ResizeBuffers(w, h, dpi);
        }
    }

    /// <summary>
    /// Even without border and title bar the presenter keeps a 3 px dialog frame, which window capture would
    /// include; without it the captured window is exactly the region.
    /// </summary>
    private void RemoveFrame()
    {
        const int frame = (int)(WINDOW_STYLE.WS_DLGFRAME | WINDOW_STYLE.WS_BORDER | WINDOW_STYLE.WS_THICKFRAME);
        int style = PInvoke.GetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        if ((style & frame) == 0) return;
        PInvoke.SetWindowLong(Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style & ~frame);
        PInvoke.SetWindowPos(Handle, HWND.Null, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private void MoveOffScreen()
    {
        var screen = Monitors.VirtualScreen;
        AppWindow.Move(new PointInt32(screen.Left - AppWindow.Size.Width - OffScreenGap, screen.Top));
    }

    /// <summary>
    /// Takes the button off the taskbar without changing the window's styles (which would also drop it from
    /// share pickers). Best effort.
    /// </summary>
    private unsafe void RemoveTaskbarButton()
    {
        try
        {
            PInvoke.CoCreateInstance(typeof(TaskbarList).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out ITaskbarList* list)
                .ThrowOnFailure();
            try
            {
                list->HrInit();
                list->DeleteTab(Handle);
            }
            finally
            {
                list->Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }
}

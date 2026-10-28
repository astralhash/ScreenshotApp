using System.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScreenshotApp.Capture;
using ScreenshotApp.Services;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace ScreenshotApp.Sharing;

/// <summary>
/// A plain window that shows a live screen region, for apps (Teams) that can share a window but not a region.
/// It has no title bar so the shared picture is just the region; drag it to move, right-click for options.
/// It can sit behind other windows (window sharing still sees it) but must not be minimized.
/// </summary>
internal sealed class MirrorWindow : Window
{
    private const string WindowTitle = "Shared region";

    private readonly CanvasDevice _device;
    private readonly CanvasSwapChainPanel _panel = new();
    private readonly object _gate = new(); // swap chain: drawn on the capture thread, resized on the UI thread
    private readonly HWND _hwnd;
    private CanvasSwapChain? _swapChain;
    private Size _regionSize;
    private PointInt32? _dragOffset; // cursor minus window position while dragging
    private bool _closing;

    public event Action? ChangeRegionRequested;
    public event Action? StopRequested;

    public MirrorWindow(CanvasDevice device)
    {
        _device = device;
        Title = WindowTitle;
        _hwnd = new HWND(WindowNative.GetWindowHandle(this));
        AppIcon.Apply(AppWindow);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsMinimizable = false; // a minimized window gives window sharing nothing to show
            presenter.IsMaximizable = false;
        }

        var root = new Grid { Background = new SolidColorBrush(Colors.Black), Children = { _panel } };
        root.ContextFlyout = BuildMenu();
        root.PointerPressed += OnPointerPressed;
        root.PointerMoved += OnPointerMoved;
        root.PointerReleased += OnPointerReleased;
        root.PointerCaptureLost += (_, _) => _dragOffset = null;
        Content = root;

        _panel.SizeChanged += (_, _) => UpdateSwapChain();
        _panel.CompositionScaleChanged += (_, _) => UpdateSwapChain();
        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true; // Alt+F4: end the share; the owner closes the window, after this event has returned
            DispatcherQueue.TryEnqueue(() => StopRequested?.Invoke());
        };
    }

    /// <summary>Sizes the window to show <paramref name="region"/> 1:1 (if it fits) and puts it where it does not cover it.</summary>
    public void ShowFor(Rectangle region)
    {
        _regionSize = region.Size;
        FitToRegion(region);
        AppWindow.Show(activateWindow: false);
        if (Overlaps(region)) SendToBack(); // otherwise the region would show the window showing the region...
    }

    /// <summary>Another region in the same window: resized to it, but left where the user put it.</summary>
    public void SetRegion(Rectangle region)
    {
        _regionSize = region.Size;
        FitToRegion(null);
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
            // 1:1 on screen stays pixel-exact; anything else is scaled smoothly.
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

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Change region…", () => ChangeRegionRequested?.Invoke()));
        menu.Items.Add(Item("Actual size", () => FitToRegion(null)));
        menu.Items.Add(Item("Send to back", SendToBack));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Stop sharing", () => StopRequested?.Invoke()));
        return menu;

        static MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            return item;
        }
    }

    /// <summary>
    /// Client area = region size (capped to the work area). With a region given, also places the window: on
    /// another monitor if there is one, else beside the region, else in the corner of the work area.
    /// </summary>
    private void FitToRegion(Rectangle? region)
    {
        var display = region is { } r0
            ? PickDisplay(r0)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var wa = display.WorkArea;
        double fit = Math.Min(1.0, Math.Min(wa.Width * 0.9 / _regionSize.Width, wa.Height * 0.9 / _regionSize.Height));
        var want = new SizeInt32(Math.Max(1, (int)(_regionSize.Width * fit)), Math.Max(1, (int)(_regionSize.Height * fit)));
        AppWindow.ResizeClient(want);
        // ResizeClient still allows for the title bar this window does not have: correct by the real client size.
        if (PInvoke.GetClientRect(_hwnd, out var client) && (client.right != want.Width || client.bottom != want.Height))
        {
            var outer = AppWindow.Size;
            AppWindow.Resize(new SizeInt32(outer.Width + want.Width - client.right, outer.Height + want.Height - client.bottom));
        }
        if (region is not { } r) return;

        var size = AppWindow.Size; // outer size, now including the border
        var area = new Rectangle(wa.X, wa.Y, wa.Width, wa.Height);
        Point pos;
        if (!area.IntersectsWith(r))
            pos = new Point(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2);
        else if (area.Right - r.Right >= size.Width + 16)
            pos = new Point(r.Right + 16, Math.Clamp(r.Y, area.Y, area.Bottom - size.Height));
        else if (r.Left - area.Left >= size.Width + 16)
            pos = new Point(r.Left - 16 - size.Width, Math.Clamp(r.Y, area.Y, area.Bottom - size.Height));
        else if (area.Bottom - r.Bottom >= size.Height + 16)
            pos = new Point(Math.Clamp(r.X, area.X, area.Right - size.Width), r.Bottom + 16);
        else
            pos = new Point(area.Right - size.Width, area.Bottom - size.Height);
        AppWindow.Move(new PointInt32(pos.X, pos.Y));
    }

    /// <summary>A monitor other than the region's, when there is one.</summary>
    private static DisplayArea PickDisplay(Rectangle region)
    {
        var center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        DisplayArea? same = null;
        var all = DisplayArea.FindAll(); // indexed: enumerating this projected list throws InvalidCastException
        for (int i = 0; i < all.Count; i++)
        {
            var d = all[i];
            var b = d.OuterBounds;
            if (new Rectangle(b.X, b.Y, b.Width, b.Height).Contains(center)) same = d;
            else return d;
        }
        return same ?? DisplayArea.Primary;
    }

    private bool Overlaps(Rectangle region)
    {
        var p = AppWindow.Position;
        var s = AppWindow.Size;
        return new Rectangle(p.X, p.Y, s.Width, s.Height).IntersectsWith(region);
    }

    private void SendToBack() =>
        PInvoke.SetWindowPos(_hwnd, new HWND(1), 0, 0, 0, 0, // HWND_BOTTOM
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    // ---- drag anywhere to move (there is no title bar) ----

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        PInvoke.GetCursorPos(out var cursor);
        var pos = AppWindow.Position;
        _dragOffset = new PointInt32(cursor.X - pos.X, cursor.Y - pos.Y);
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragOffset is not { } offset) return;
        PInvoke.GetCursorPos(out var cursor);
        AppWindow.Move(new PointInt32(cursor.X - offset.X, cursor.Y - offset.Y));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragOffset = null;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }
}

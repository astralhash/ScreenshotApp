using System.Drawing;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor.Model;
using ScreenshotApp.Services;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Color = Windows.UI.Color;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace ScreenshotApp.Editor;

/// <summary>
/// The one editor window. Closing it only hides it (WinUI would exit with the last window), so the app keeps
/// running in the tray and the clips stay in the film strip; the next capture or "Open Editor" shows it again.
/// </summary>
public sealed partial class EditorWindow : Window
{
    private const double MinZoom = 0.05, MaxZoom = 32, CanvasPadding = 24;

    private readonly nint _hwnd;
    private EditorDocument? _doc;
    private CanvasBitmap? _bitmap;

    // View state of the current shot. _zoom is physical screen pixels per image pixel;
    // _offset is the image origin in DIPs. Saved to / restored from the document on switch.
    private double _zoom = 1;
    private Vector2 _offset;
    private bool _fitMode = true;

    private bool _spaceDown, _panning, _drawing, _sized;
    private RectangleF _viewRect;
    private bool _pressHandledByHost;

    // In-place text editing: the annotation being edited and whether it is new (not yet in the document).
    private TextAnnotation? _editing;
    private bool _editingIsNew;
    private Point _panLast;

    internal EditorViewModel ViewModel { get; }

    internal EditorWindow(SettingsService settings, ClipStore store)
    {
        ViewModel = new EditorViewModel(settings, store, PickSavePathAsync);
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(EditorViewModel.SelectedShot)) return;
            SwitchDocument(ViewModel.SelectedShot);
            if (ViewModel.SelectedShot is { } shot) FilmStrip.ScrollIntoView(shot);
        };
        ViewModel.LastShotClosed += (_, _) =>
        {
            if (settings.Current.CloseEditorWhenEmpty) AppWindow.Hide();
        };
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "ScreenshotApp Editor";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        AppWindow.Closing += OnClosing;
        AppIcon.Apply(AppWindow);

        BuildSwatches();
        UpdateToolButtons();
        ViewModel.Interaction.Changed += () =>
        {
            SyncViewRect();
            Canvas.Invalidate();
        };
        ViewModel.Interaction.TextEditRequested += BeginTextEdit;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Tool))
            {
                CommitTextEdit();
                UpdateToolButtons();
                SyncViewRect();
            }
            else if (e.PropertyName == nameof(EditorViewModel.StrokeColor)) UpdateSwatchSelection();
        };

        // Any press outside the text editor (toolbar, film strip...) finishes the edit.
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_AnyPointerPressed), true);
    }

    // ---- tools and properties bar ----

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse<ToolKind>(name, out var tool))
            ViewModel.Tool = tool;
        UpdateToolButtons(); // keep the clicked button checked even when re-clicking the active tool
    }

    private void UpdateToolButtons()
    {
        foreach (var child in ToolPanel.Children)
            if (child is ToggleButton { Tag: string name } button)
                button.IsChecked = name == ViewModel.Tool.ToString();
    }

    private void BuildSwatches()
    {
        int index = 0;
        foreach (var color in EditorViewModel.Palette)
        {
            var swatch = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(2),
                AllowFocusOnInteraction = false,
                Tag = color,
            };
            ToolTipService.SetToolTip(swatch, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
            swatch.Click += (s, _) => ViewModel.StrokeColor = (Color)((FrameworkElement)s).Tag;
            SwatchPanel.Children.Insert(index++, swatch);
        }
        UpdateSwatchSelection();
    }

    private void UpdateSwatchSelection()
    {
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var outline = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        foreach (var child in SwatchPanel.Children)
            if (child is Button { Tag: Color c } swatch)
                swatch.BorderBrush = c == ViewModel.StrokeColor ? accent : outline;
    }

    private void CustomPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args) =>
        ViewModel.StrokeColor = args.NewColor;

    internal bool HasShots => ViewModel.Shots.Count > 0;

    /// <summary>Adds a shot to the film strip (newest first) and shows the window.</summary>
    internal EditorDocument AddShot(CapturedImage image)
    {
        var doc = ViewModel.AddShot(image);
        ShowEditor();
        return doc;
    }

    /// <summary>Loads clips kept from a previous run without showing the window.</summary>
    internal void Restore(IEnumerable<EditorDocument> clips) => ViewModel.Restore(clips);

    /// <summary>
    /// Shows the window and raises it. The first time with a clip it is sized to that clip; with no clips
    /// (start page) it uses a default size.
    /// </summary>
    internal void ShowEditor()
    {
        if (!_sized)
        {
            if (ViewModel.SelectedShot is { } shot)
            {
                SizeToImage(shot.Width, shot.Height);
                _sized = true;
            }
            else
            {
                SizeToImage(0, 0); // clamps up to the minimum window size
            }
        }
        KeepOutOfAvoidArea();
        BringToFront();
    }

    private System.Drawing.Rectangle? _avoidArea;

    /// <summary>
    /// A screen area the window must not cover (the region being shared, physical pixels), or null. The window is
    /// moved out of it whenever it is shown, and right away if it is visible now.
    /// </summary>
    internal System.Drawing.Rectangle? AvoidArea
    {
        get => _avoidArea;
        set
        {
            _avoidArea = value;
            if (IsShown) KeepOutOfAvoidArea();
        }
    }

    /// <summary>
    /// Moves the window into the free space beside <see cref="AvoidArea"/>: one it fits in if any (preferring the
    /// monitor under the cursor), else the largest, shrinking the window to it.
    /// </summary>
    private void KeepOutOfAvoidArea()
    {
        if (_avoidArea is not { } area) return;
        var avoid = System.Drawing.Rectangle.Inflate(area, 8, 8); // keep clear of the red outline too
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        if (!new System.Drawing.Rectangle(pos.X, pos.Y, size.Width, size.Height).IntersectsWith(avoid)) return;

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter)
        {
            presenter.Restore();
            size = AppWindow.Size;
        }

        PInvoke.GetCursorPos(out var pt);
        System.Drawing.Rectangle? best = null;
        (bool Fits, bool OnCursor, long Room) bestScore = default;
        foreach (var monitor in Monitors.GetAll())
        {
            var wa = DisplayArea.GetFromRect(new RectInt32(monitor.X, monitor.Y, monitor.Width, monitor.Height),
                DisplayAreaFallback.Nearest).WorkArea;
            var work = new System.Drawing.Rectangle(wa.X, wa.Y, wa.Width, wa.Height);
            bool onCursor = work.Contains(pt.X, pt.Y);
            foreach (var free in FreeSpaces(work, avoid))
            {
                var score = (free.Width >= size.Width && free.Height >= size.Height, onCursor, (long)free.Width * free.Height);
                if (best is null || score.CompareTo(bestScore) > 0)
                {
                    best = free;
                    bestScore = score;
                }
            }
        }
        if (best is not { } target) return; // the shared region covers every screen

        int w = Math.Min(size.Width, target.Width), h = Math.Min(size.Height, target.Height);
        AppWindow.MoveAndResize(new RectInt32(target.X + (target.Width - w) / 2, target.Y + (target.Height - h) / 2, w, h));
    }

    /// <summary>The parts of <paramref name="work"/> left, right, above and below <paramref name="avoid"/>.</summary>
    private static IEnumerable<System.Drawing.Rectangle> FreeSpaces(System.Drawing.Rectangle work, System.Drawing.Rectangle avoid)
    {
        if (!work.IntersectsWith(avoid))
        {
            yield return work;
            yield break;
        }
        if (avoid.Left > work.Left) yield return System.Drawing.Rectangle.FromLTRB(work.Left, work.Top, avoid.Left, work.Bottom);
        if (avoid.Right < work.Right) yield return System.Drawing.Rectangle.FromLTRB(avoid.Right, work.Top, work.Right, work.Bottom);
        if (avoid.Top > work.Top) yield return System.Drawing.Rectangle.FromLTRB(work.Left, work.Top, work.Right, avoid.Top);
        if (avoid.Bottom < work.Bottom) yield return System.Drawing.Rectangle.FromLTRB(work.Left, avoid.Bottom, work.Right, work.Bottom);
    }

    /// <summary>Hides the window so it does not appear in a capture that was started from its buttons.</summary>
    internal void HideForCapture() => AppWindow.Hide();

    internal bool IsShown => AppWindow.IsVisible;

    /// <summary>The start-page buttons ask the app to run a capture (the app hides this window first).</summary>
    internal event Action<CaptureKind>? CaptureRequested;

    internal event Action? SettingsRequested;

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name } && Enum.TryParse<CaptureKind>(name, out var kind))
            CaptureRequested?.Invoke(kind);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    // ---- adding existing images ----

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        foreach (var ext in ImageExtensions) picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null) return;
        foreach (var file in files) await AddImageFileAsync(file.Path);
    }

    /// <summary>
    /// Adds an image from disk as a new clip. It counts as unsaved (no saved path), so Ctrl+S writes a new file
    /// instead of overwriting the original.
    /// </summary>
    internal async Task AddImageFileAsync(string path)
    {
        try
        {
            var image = await ImageSaver.DecodeAsync(path);
            ViewModel.AddShot(image);
            ShowEditor();
            ViewModel.ShowStatus("Added " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ViewModel.ShowStatus("Could not open " + Path.GetFileName(path));
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add as clip";
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            // Dropped in order, so the last one ends up newest (leftmost) like consecutive captures.
            foreach (var file in items.OfType<StorageFile>().Where(f => IsImageFile(f.Path)))
                await AddImageFileAsync(file.Path);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Shows, activates and raises the window above others (new windows may otherwise open behind).</summary>
    private void BringToFront()
    {
        AppWindow.Show();
        Activate();
        HWND hwnd = new(_hwnd);
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
        PInvoke.SetWindowPos(hwnd, new HWND(-1), 0, 0, 0, 0, flags);  // HWND_TOPMOST
        PInvoke.SetWindowPos(hwnd, new HWND(-2), 0, 0, 0, 0, flags);  // HWND_NOTOPMOST: raised, not pinned
        PInvoke.SetForegroundWindow(hwnd);
    }

    private double Scale => Root.XamlRoot?.RasterizationScale ?? PInvoke.GetDpiForWindow(new(_hwnd)) / 96.0;

    /// <summary>Starts a session: image size plus chrome, capped to 90% of the work area under the cursor, centered.</summary>
    private void SizeToImage(int imageWidth, int imageHeight)
    {
        PInvoke.GetCursorPos(out var pt);
        var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest).WorkArea;
        double s = PInvoke.GetDpiForWindow(new(_hwnd)) / 96.0;

        int chromeH = (int)((32 + 52 + 40 + 44 + 123) * s), pad = (int)(CanvasPadding * 2 * s); // title + command bar + film strip
        int w = Math.Clamp(imageWidth + pad, (int)(760 * s), (int)(area.Width * 0.9));
        int h = Math.Clamp(imageHeight + pad + chromeH, (int)(560 * s), (int)(area.Height * 0.9));

        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true; // never destroy the window: the app lives in the tray
        // Hide only: the clips stay in the film strip until they are removed with their × button.
        AppWindow.Hide();
    }

    private async Task<string?> PickSavePathAsync()
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedFileName = ImageSaver.ExpandPattern(App.Settings.Current.FilenamePattern, DateTime.Now);
        bool jpgFirst = App.Settings.Current.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase);
        var png = new KeyValuePair<string, IList<string>>("PNG image", new List<string> { ".png" });
        var jpg = new KeyValuePair<string, IList<string>>("JPEG image", new List<string> { ".jpg" });
        foreach (var choice in jpgFirst ? new[] { jpg, png } : new[] { png, jpg })
            picker.FileTypeChoices.Add(choice);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    // ---- documents ----

    private async void SwitchDocument(EditorDocument? doc)
    {
        if (_doc is not null)
        {
            _doc.Zoom = _zoom;
            _doc.Offset = _offset;
            _doc.FitMode = _fitMode;
        }
        _doc = doc;
        _bitmap?.Dispose();
        _bitmap = null;
        if (doc is null)
        {
            Canvas.Invalidate();
            return;
        }

        _zoom = doc.Zoom;
        _offset = doc.Offset;
        _fitMode = doc.FitMode;
        _viewRect = ViewRect;
        if (_fitMode) FitToWindow(); else Redraw();

        // Only the shot being edited keeps raw pixels; the rest stay as compressed PNGs.
        if (!doc.IsLoaded)
        {
            try
            {
                await doc.EnsureLoadedAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ViewModel.ShowStatus("Could not load this clip");
                return;
            }
            if (_doc != doc) return; // the user moved on while it was decoding
        }
        BuildBitmap();
        Canvas.Invalidate();

        bool released = false;
        foreach (var other in ViewModel.Shots)
        {
            if (other == doc || !other.IsLoaded) continue;
            other.Unload();
            released = true;
        }
        // The released pixel arrays are large-object-heap garbage; collect now instead of letting
        // the working set balloon until the runtime gets around to it.
        if (released) GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
    }

    private void BuildBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = _doc is not { IsLoaded: true }
            ? null
            : CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), _doc.Image.Pixels,
                _doc.Image.Width, _doc.Image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96);
    }

    // ---- film strip ----

    private void RemoveShot_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is EditorDocument doc) ViewModel.RemoveShot(doc);
    }

    /// <summary>Right-click menu: acts on that clip directly, without selecting it first.</summary>
    private void Thumb_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: EditorDocument clip } thumb) return;

        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Copy", "", () => _ = ViewModel.CopyShotAsync(clip)));
        menu.Items.Add(MenuItem("Save", "", () => _ = ViewModel.SaveShotAsync(clip)));
        menu.Items.Add(MenuItem("Save As…", "", () => _ = ViewModel.SaveShotAsAsync(clip)));
        menu.Items.Add(MenuItem("Close", "", () => ViewModel.RemoveShot(clip)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuItem("Remove all clips", "", () => _ = RemoveAllClipsAsync()));

        if (args.TryGetPosition(thumb, out var position)) menu.ShowAt(thumb, position);
        else menu.ShowAt(thumb);
        args.Handled = true;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action click)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons") },
        };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Asks first (unless the user ticked "don't ask again"), then closes every clip.</summary>
    private async Task RemoveAllClipsAsync()
    {
        var settings = App.Settings;
        if (settings.Current.ConfirmRemoveAll)
        {
            var dontAsk = new CheckBox { Content = "Don't ask me again" };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Remove all clips?",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"This closes all {ViewModel.Shots.Count} clips in the film strip. " +
                                   "Clips that are not saved, and their edits, will be lost.",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        dontAsk,
                    },
                },
                PrimaryButtonText = "Remove all",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            if (dontAsk.IsChecked == true)
            {
                settings.Current.ConfirmRemoveAll = false;
                try { settings.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
            }
        }
        ViewModel.RemoveAllShots();
    }

    private EditorDocument? _hoveredClip;

    private void Thumb_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EditorDocument doc }) _hoveredClip = doc;
        SetRemoveVisible(sender, true);
    }

    private void Thumb_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EditorDocument doc } && doc == _hoveredClip) _hoveredClip = null;
        SetRemoveVisible(sender, false);
    }

    private static void SetRemoveVisible(object thumb, bool visible)
    {
        if (thumb is Grid { Children: [_, Button remove] })
            remove.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Lets a plain mouse wheel scroll the film strip sideways.</summary>
    private void FilmStrip_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var scroller = FindScrollViewer(FilmStrip);
        if (scroller is null) return;
        int delta = e.GetCurrentPoint(FilmStrip).Properties.MouseWheelDelta;
        scroller.ChangeView(scroller.HorizontalOffset - delta, null, null);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    // ---- canvas ----

    private void Canvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args) =>
        BuildBitmap(); // also runs after a device loss

    /// <summary>
    /// The part of the image shown and exported: the full image while cropping (so the box can be adjusted),
    /// otherwise the applied crop if there is one.
    /// </summary>
    private RectangleF ViewRect =>
        _doc is null ? RectangleF.Empty
        : ViewModel.Tool == ToolKind.Crop || _doc.Crop.Rect is null ? new RectangleF(0, 0, _doc.Width, _doc.Height)
        : _doc.Crop.Rect.Value;

    /// <summary>Refits when the visible region changed (a crop was applied, undone, or the Crop tool toggled).</summary>
    private void SyncViewRect()
    {
        var view = ViewRect;
        if (view == _viewRect) return;
        _viewRect = view;
        FitToWindow();
    }

    private float PixelScale => (float)(_zoom / Scale);

    /// <summary>Image pixels to Host DIPs.</summary>
    private Vector2 ToScreen(Vector2 p) => _offset + (p - new Vector2(_viewRect.X, _viewRect.Y)) * PixelScale;

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_bitmap is null || _doc is null) return;
        var ds = args.DrawingSession;
        var view = ViewRect;
        float s = PixelScale;
        float w = view.Width * s, h = view.Height * s;

        // Soft drop shadow: a few expanding translucent rects.
        for (int i = 1; i <= 8; i++)
            ds.FillRectangle(_offset.X - i, _offset.Y - i + 3, w + 2 * i, h + 2 * i,
                Color.FromArgb((byte)(16 - i * 2 > 0 ? 16 - i * 2 : 1), 0, 0, 0));

        // Everything is drawn in image pixels; the transform maps the visible region onto the canvas.
        var image = new Rect(0, 0, _doc.Width, _doc.Height);
        ds.Transform = Matrix3x2.CreateTranslation(-view.X, -view.Y) * Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(_offset);
        using (ds.CreateLayer(1f, new Rect(view.X, view.Y, view.Width, view.Height)))
        {
            ds.DrawImage(_bitmap, image, image, 1f,
                _zoom >= 2 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.HighQualityCubic);

            var ctx = new RenderContext(_bitmap);
            var editing = ViewModel.Interaction.EditingId;
            foreach (var a in _doc.Annotations)
                if (a.Id != editing) a.Render(ds, ctx);
            ViewModel.Interaction.Preview?.Render(ds, ctx);
        }

        ds.Transform = Matrix3x2.Identity;
        if (ViewModel.Tool == ToolKind.Crop) DrawCropOverlay(ds, s);
        else if (ViewModel.Interaction.Selected is { } selected) DrawSelection(ds, selected);
    }

    /// <summary>Selection outline and handles at a constant on-screen size.</summary>
    private void DrawSelection(CanvasDrawingSession ds, Annotation a)
    {
        var accent = Color.FromArgb(255, 0, 120, 212);
        float s = PixelScale;

        if (a.ShowBoundsWhenSelected)
        {
            var b = a.Bounds;
            using var dashed = new Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle
            {
                DashStyle = Microsoft.Graphics.Canvas.Geometry.CanvasDashStyle.Dash,
            };
            var tl = ToScreen(new Vector2(b.Left, b.Top));
            ds.DrawRectangle(tl.X, tl.Y, b.Width * s, b.Height * s, accent, 1f, dashed);
        }

        for (int i = 0; i < a.HandleCount; i++)
            DrawHandle(ds, ToScreen(a.GetHandle(i)), accent);
    }

    private static void DrawHandle(CanvasDrawingSession ds, Vector2 c, Color accent)
    {
        ds.FillRectangle(c.X - 4, c.Y - 4, 8, 8, Colors.White);
        ds.DrawRectangle(c.X - 4, c.Y - 4, 8, 8, accent, 1.5f);
    }

    /// <summary>Dims everything outside the pending crop box and shows its handles and size.</summary>
    private void DrawCropOverlay(CanvasDrawingSession ds, float s)
    {
        if (_doc is null || ViewModel.Interaction.PendingCrop is not { } crop) return;

        var accent = Color.FromArgb(255, 0, 120, 212);
        var dim = Color.FromArgb(150, 0, 0, 0);
        var imageTl = ToScreen(Vector2.Zero);
        float iw = _doc.Width * s, ih = _doc.Height * s;
        var tl = ToScreen(new Vector2(crop.Left, crop.Top));
        float cw = crop.Width * s, ch = crop.Height * s;

        // Four dim bands around the box (inside the image area).
        ds.FillRectangle(imageTl.X, imageTl.Y, iw, tl.Y - imageTl.Y, dim);
        ds.FillRectangle(imageTl.X, tl.Y + ch, iw, imageTl.Y + ih - (tl.Y + ch), dim);
        ds.FillRectangle(imageTl.X, tl.Y, tl.X - imageTl.X, ch, dim);
        ds.FillRectangle(tl.X + cw, tl.Y, imageTl.X + iw - (tl.X + cw), ch, dim);

        ds.DrawRectangle(tl.X, tl.Y, cw, ch, Colors.White, 1.5f);
        for (int i = 0; i < 8; i++)
            DrawHandle(ds, ToScreen(HandlePoint(crop, i)), accent);

        // Size readout just above the box (or inside when there is no room).
        string label = $"{(int)Math.Round(crop.Width)} × {(int)Math.Round(crop.Height)}";
        using var format = new Microsoft.Graphics.Canvas.Text.CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 12 };
        float ly = tl.Y > 28 ? tl.Y - 26 : tl.Y + 6;
        ds.FillRoundedRectangle(tl.X, ly, 76, 20, 4, 4, Color.FromArgb(220, 32, 32, 32));
        ds.DrawText(label, tl.X + 8, ly + 2, Colors.White, format);
    }

    private static Vector2 HandlePoint(RectangleF r, int i) => HandleGeometry.Get(r, i);

    private Vector2 Viewport => new((float)Host.ActualWidth, (float)Host.ActualHeight);

    private void ApplyZoomText() => ViewModel.ZoomText = $"{Math.Round(_zoom * 100)}%";

    /// <summary>Centers the visible region on an axis when it fits, otherwise keeps it overlapping the viewport.</summary>
    private void ClampOffset()
    {
        if (_doc is null) return;
        float s = PixelScale;
        var view = Viewport;
        float iw = _viewRect.Width * s, ih = _viewRect.Height * s, pad = (float)CanvasPadding;
        _offset.X = iw + 2 * pad <= view.X ? (view.X - iw) / 2 : Math.Clamp(_offset.X, view.X - iw - pad, pad);
        _offset.Y = ih + 2 * pad <= view.Y ? (view.Y - ih) / 2 : Math.Clamp(_offset.Y, view.Y - ih - pad, pad);
        PositionTextEditor();
    }

    private void Redraw()
    {
        ClampOffset();
        ApplyZoomText();
        Canvas.Invalidate();
    }

    private void FitToWindow()
    {
        _fitMode = true;
        var view = Viewport;
        if (_doc is null || view.X <= 0 || view.Y <= 0) return; // SizeChanged refits once laid out
        _viewRect = ViewRect;
        double fx = (view.X - 2 * CanvasPadding) * Scale / _viewRect.Width;
        double fy = (view.Y - 2 * CanvasPadding) * Scale / _viewRect.Height;
        _zoom = Math.Clamp(Math.Min(Math.Min(fx, fy), 1.0), MinZoom, MaxZoom); // never upscale on fit
        Redraw();
    }
    /// <summary>Zooms keeping the image point under <paramref name="anchor"/> (host DIPs) fixed.</summary>
    private void ZoomTo(double newZoom, Vector2 anchor)
    {
        if (_doc is null) return;
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        double oldS = _zoom / Scale, newS = newZoom / Scale;
        Vector2 imagePoint = (anchor - _offset) / (float)oldS;
        _zoom = newZoom;
        _offset = anchor - imagePoint * (float)newS;
        _fitMode = false;
        Redraw();
    }

    private Vector2 Center => Viewport / 2;

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitMode) FitToWindow();
        else Redraw();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomTo(_zoom * 1.25, Center);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomTo(_zoom / 1.25, Center);
    private void Zoom100_Click(object sender, RoutedEventArgs e) => ZoomTo(1.0, Center);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    // ---- pointer / keyboard ----

    private void Host_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Host).Properties;
        int notches = props.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var p = e.GetCurrentPoint(Host).Position;
            ZoomTo(_zoom * Math.Pow(1.0015, notches), new Vector2((float)p.X, (float)p.Y));
        }
        else
        {
            float d = notches / 120f * 60f;
            if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)) _offset.X += d; else _offset.Y += d;
            _fitMode = false;
            Redraw();
        }
        e.Handled = true;
    }

    private void Host_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressHandledByHost = true;
        CommitTextEdit(); // clicking the canvas finishes any text being typed
        var pt = e.GetCurrentPoint(Host);
        bool middle = pt.Properties.IsMiddleButtonPressed;
        if (middle || (_spaceDown && pt.Properties.IsLeftButtonPressed))
        {
            _panning = true;
            _panLast = pt.Position;
            Host.CapturePointer(e.Pointer);
            Host.SetCursor(InputSystemCursorShape.SizeAll);
            e.Handled = true;
            return;
        }

        if (pt.Properties.IsLeftButtonPressed && _doc is not null)
        {
            Host.CapturePointer(e.Pointer);
            _drawing = true;
            ViewModel.Interaction.DipsPerPixel = (float)(_zoom / Scale);
            ViewModel.Interaction.PointerDown(ToImage(pt.Position), IsShift(e));
            e.Handled = true;
        }
    }

    private void Host_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Host).Position;
        if (_panning)
        {
            _offset += new Vector2((float)(p.X - _panLast.X), (float)(p.Y - _panLast.Y));
            _panLast = p;
            _fitMode = false;
            Redraw();
        }
        else if (_drawing)
        {
            ViewModel.Interaction.PointerMove(ToImage(p), IsShift(e));
        }
        else if (!_spaceDown && _doc is not null)
        {
            ViewModel.Interaction.DipsPerPixel = (float)(_zoom / Scale);
            Host.SetCursor(ToShape(ViewModel.Interaction.HoverCursor(ToImage(p))));
        }
    }

    private void Host_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drawing)
        {
            _drawing = false;
            Host.ReleasePointerCaptures();
            ViewModel.Interaction.PointerUp(ToImage(e.GetCurrentPoint(Host).Position), IsShift(e));
            return;
        }
        if (!_panning) return;
        _panning = false;
        Host.ReleasePointerCaptures();
        Host.SetCursor(_spaceDown ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.Arrow);
    }

    /// <summary>Host position (DIPs) to image pixels.</summary>
    private Vector2 ToImage(Point p) =>
        (new Vector2((float)p.X, (float)p.Y) - _offset) / PixelScale + new Vector2(_viewRect.X, _viewRect.Y);

    private static bool IsShift(PointerRoutedEventArgs e) => e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);

    private static InputSystemCursorShape ToShape(CursorKind kind) => kind switch
    {
        CursorKind.Cross => InputSystemCursorShape.Cross,
        CursorKind.Move => InputSystemCursorShape.SizeAll,
        CursorKind.ResizeNwSe => InputSystemCursorShape.SizeNorthwestSoutheast,
        CursorKind.ResizeNeSw => InputSystemCursorShape.SizeNortheastSouthwest,
        CursorKind.ResizeNs => InputSystemCursorShape.SizeNorthSouth,
        CursorKind.ResizeWe => InputSystemCursorShape.SizeWestEast,
        _ => InputSystemCursorShape.Arrow,
    };

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox) return;
        if (e.Key == VirtualKey.Delete && _hoveredClip is { } hovered)
        {
            _hoveredClip = null;
            ViewModel.RemoveShot(hovered);
            e.Handled = true;
            return;
        }
        if (e.Key is VirtualKey.Left or VirtualKey.Right && ViewModel.SelectedShot is { } current
            && !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            && !InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            // Film strip is newest-left, so Left = previous index.
            int next = ViewModel.Shots.IndexOf(current) + (e.Key == VirtualKey.Left ? -1 : 1);
            if (next >= 0 && next < ViewModel.Shots.Count) ViewModel.SelectedShot = ViewModel.Shots[next];
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Delete && ViewModel.Interaction.Selected is null && ViewModel.SelectedShot is { } open)
        {
            ViewModel.RemoveShot(open); // no annotation selected: Delete closes the open clip
            e.Handled = true;
            return;
        }
        if (HandleToolKey(e)) return;
        if (e.Key != VirtualKey.Space) return;
        if (!_spaceDown)
        {
            _spaceDown = true;
            Host.SetCursor(InputSystemCursorShape.SizeAll);
        }
        e.Handled = true; // also stops Space from "clicking" a focused button
    }

    // ---- in-place text editing ----

    private void BeginTextEdit(TextAnnotation annotation, bool isNew)
    {
        CommitTextEdit();
        if (_doc is null) return;

        _editing = annotation;
        _editingIsNew = isNew;
        ViewModel.Interaction.EditingId = annotation.Id; // the canvas stops drawing it while the box shows it

        TextEditor.Text = annotation.Text;
        TextEditor.Foreground = new SolidColorBrush(annotation.Color);
        TextEditor.Visibility = Visibility.Visible;
        PositionTextEditor();
        TextEditor.Focus(FocusState.Programmatic);
        if (!isNew) TextEditor.SelectAll();
        Canvas.Invalidate();
    }

    private void PositionTextEditor()
    {
        if (_editing is null) return;
        var p = ToScreen(_editing.Position);
        TextEditor.FontSize = _editing.FontSize * PixelScale;
        TextEditor.Margin = new Thickness(p.X - 1, p.Y - 1, 0, 0); // 1 DIP border
    }

    /// <summary>Finishes the edit: adds, changes or (when emptied) deletes the text annotation.</summary>
    private void CommitTextEdit()
    {
        if (_editing is null) return;
        var annotation = _editing;
        bool isNew = _editingIsNew;
        string text = TextEditor.Text;
        _editing = null;
        TextEditor.Visibility = Visibility.Collapsed;
        ViewModel.Interaction.CommitText(annotation, text, isNew);
    }

    private void TextEditor_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape || (ctrl && e.Key == VirtualKey.Enter))
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    private void TextEditor_LostFocus(object sender, RoutedEventArgs e) => CommitTextEdit();

    private void Root_AnyPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // The canvas handles its own presses (including starting a new text edit).
        if (_pressHandledByHost)
        {
            _pressHandledByHost = false;
            return;
        }
        if (_editing is null) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d == TextEditor) return;
        CommitTextEdit();
    }

    private void Host_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_doc is null) return;
        ViewModel.Interaction.DipsPerPixel = PixelScale;
        ViewModel.Interaction.TryEditTextAt(ToImage(e.GetPosition(Host)));
    }

    private void PixelateMode_Click(object sender, RoutedEventArgs e) => SetBlurMode(BlurMode.Pixelate);
    private void BlurMode_Click(object sender, RoutedEventArgs e) => SetBlurMode(BlurMode.Blur);
    private void SolidMode_Click(object sender, RoutedEventArgs e) => SetBlurMode(BlurMode.Solid);

    private void SetBlurMode(BlurMode mode)
    {
        ViewModel.BlurMode = mode;
        PixelateToggle.IsChecked = ViewModel.IsPixelate; // a re-click must not uncheck the active mode
        BlurToggle.IsChecked = ViewModel.IsBlur;
        SolidToggle.IsChecked = ViewModel.IsSolid;
    }

    /// <summary>Single-letter tool shortcuts (no modifiers), Delete and Escape.</summary>
    private bool HandleToolKey(KeyRoutedEventArgs e)
    {
        bool modified = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                        || InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (modified) return false;

        ToolKind? tool = e.Key switch
        {
            VirtualKey.V => ToolKind.Select,
            VirtualKey.A => ToolKind.Arrow,
            VirtualKey.R => ToolKind.Rectangle,
            VirtualKey.L => ToolKind.Line,
            VirtualKey.P => ToolKind.Pen,
            VirtualKey.H => ToolKind.Highlighter,
            VirtualKey.T => ToolKind.Text,
            VirtualKey.N => ToolKind.Step,
            VirtualKey.B => ToolKind.Blur,
            VirtualKey.C => ToolKind.Crop,
            _ => null,
        };
        if (tool is { } t)
        {
            ViewModel.Tool = t;
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Delete)
        {
            ViewModel.Interaction.DeleteSelected();
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Enter && ViewModel.Tool == ToolKind.Crop)
        {
            ViewModel.ApplyCropCommand.Execute(null);
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Escape && ViewModel.Tool == ToolKind.Crop)
        {
            ViewModel.CancelCropCommand.Execute(null);
            e.Handled = true;
            return true;
        }
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.Interaction.Deselect();
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space) return;
        _spaceDown = false;
        if (!_panning) Host.SetCursor(InputSystemCursorShape.Arrow);
    }
}

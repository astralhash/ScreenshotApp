using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor.Model;
using ScreenshotApp.Services;
using Windows.UI;

namespace ScreenshotApp.Editor;

/// <summary>State of the single editor window: a session of shots (newest first) and the actions on the selected one.</summary>
internal sealed partial class EditorViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ClipStore _store;
    private readonly Func<Task<string?>> _pickSavePath;
    private readonly DispatcherQueueTimer _statusTimer;

    public ObservableCollection<EditorDocument> Shots { get; } = new();

    [ObservableProperty]
    public partial EditorDocument? SelectedShot { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string ZoomText { get; set; } = "100%";

    /// <summary>Tool and selection logic; the window feeds it pointer input in image coordinates.</summary>
    public EditorInteraction Interaction { get; } = new();

    public static readonly Color[] Palette =
    {
        Color.FromArgb(255, 229, 57, 53),   // red
        Color.FromArgb(255, 251, 140, 0),   // orange
        Color.FromArgb(255, 253, 216, 53),  // yellow
        Color.FromArgb(255, 67, 160, 71),   // green
        Color.FromArgb(255, 30, 136, 229),  // blue
        Color.FromArgb(255, 142, 36, 170),  // purple
        Color.FromArgb(255, 33, 33, 33),    // black
        Color.FromArgb(255, 255, 255, 255), // white
    };

    [ObservableProperty] public partial ToolKind Tool { get; set; } = ToolKind.Arrow;
    [ObservableProperty] public partial Color StrokeColor { get; set; } = Palette[0];
    [ObservableProperty] public partial double StrokeWidth { get; set; } = DefaultWidth(ToolKind.Arrow);
    [ObservableProperty] public partial bool FillShapes { get; set; }

    [ObservableProperty] public partial BlurMode BlurMode { get; set; } = BlurMode.Pixelate;

    // What the properties bar shows depends on the active tool (or the selected shape).
    [ObservableProperty] public partial bool IsCropTool { get; set; }
    [ObservableProperty] public partial bool ShowStyle { get; set; } = true;
    [ObservableProperty] public partial bool ShowFill { get; set; } = true;
    [ObservableProperty] public partial bool ShowBlurMode { get; set; }
    [ObservableProperty] public partial string WidthLabel { get; set; } = "Width";

    // Colour only matters for Solid redaction; strength only for Pixelate/Blur.
    [ObservableProperty] public partial bool ShowColor { get; set; } = true;
    [ObservableProperty] public partial bool ShowWidth { get; set; } = true;

    public bool IsPixelate => BlurMode == BlurMode.Pixelate;
    public bool IsBlur => BlurMode == BlurMode.Blur;
    public bool IsSolid => BlurMode == BlurMode.Solid;

    private bool _syncingStyle;

    partial void OnBlurModeChanged(BlurMode value)
    {
        OnPropertyChanged(nameof(IsPixelate));
        OnPropertyChanged(nameof(IsBlur));
        OnPropertyChanged(nameof(IsSolid));
        UpdateToolUi();
        PushStyle();
    }

    /// <summary>Which controls make sense: depends on the active tool, or on the selected shape when using Select.</summary>
    private void UpdateToolUi()
    {
        var kind = Tool == ToolKind.Select && Interaction.Selected is { } sel
            ? sel switch
            {
                BlurAnnotation => ToolKind.Blur,
                TextAnnotation => ToolKind.Text,
                StepAnnotation => ToolKind.Step,
                RectangleAnnotation => ToolKind.Rectangle,
                _ => ToolKind.Select,
            }
            : Tool;

        IsCropTool = Tool == ToolKind.Crop;
        ShowStyle = !IsCropTool;
        ShowFill = kind == ToolKind.Rectangle;
        ShowBlurMode = kind == ToolKind.Blur;
        bool solidRedaction = kind == ToolKind.Blur && BlurMode == BlurMode.Solid;
        bool softRedaction = kind == ToolKind.Blur && !solidRedaction;
        ShowColor = ShowStyle && !softRedaction;
        ShowWidth = ShowStyle && !solidRedaction;
        WidthLabel = kind switch
        {
            ToolKind.Blur => "Strength",
            ToolKind.Text or ToolKind.Step => "Size",
            _ => "Width",
        };
    }

    [RelayCommand]
    private void ApplyCrop()
    {
        if (Interaction.ApplyCrop()) Tool = ToolKind.Select;
    }

    /// <summary>Leaving the Crop tool without applying discards the pending box.</summary>
    [RelayCommand]
    private void CancelCrop() => Tool = ToolKind.Select;

    // Each drawing tool remembers its own width and colour (persisted in settings). Select has none.
    private readonly Dictionary<ToolKind, double> _toolWidths = new();
    private readonly Dictionary<ToolKind, Color> _toolColors = new();
    private readonly DispatcherQueueTimer _widthSaveTimer;

    private static double DefaultWidth(ToolKind tool) => tool == ToolKind.Highlighter ? 24 : 6;
    private static Color DefaultColor(ToolKind tool) => tool == ToolKind.Text ? Palette[6] : Palette[0];

    private static string ColorToString(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseColor(string? s, out Color color)
    {
        color = default;
        if (s is not { Length: 9 } || s[0] != '#' ||
            !uint.TryParse(s.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint v)) return false;
        color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    partial void OnToolChanged(ToolKind value)
    {
        Interaction.Tool = value;
        UpdateToolUi();
        if (!_toolWidths.TryGetValue(value, out double width)) return;

        // Switching tools must not restyle whatever shape is currently selected.
        _syncingStyle = true;
        StrokeWidth = width;
        StrokeColor = _toolColors[value];
        _syncingStyle = false;
        Interaction.StrokeWidth = (float)width;
        Interaction.StrokeColor = StrokeColor;
    }

    partial void OnStrokeColorChanged(Color value)
    {
        if (!_syncingStyle && _toolColors.ContainsKey(Tool))
        {
            _toolColors[Tool] = value;
            _widthSaveTimer.Stop();
            _widthSaveTimer.Start();
        }
        PushStyle();
    }

    partial void OnStrokeWidthChanged(double value)
    {
        if (!_syncingStyle && _toolWidths.ContainsKey(Tool))
        {
            _toolWidths[Tool] = value;
            _widthSaveTimer.Stop();
            _widthSaveTimer.Start(); // debounce: a slider drag changes the value many times
        }
        PushStyle();
    }

    partial void OnFillShapesChanged(bool value) => PushStyle();

    private void SaveToolStyles()
    {
        try
        {
            _settings.Current.ToolWidths = _toolWidths.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            _settings.Current.ToolColors = _toolColors.ToDictionary(kv => kv.Key.ToString(), kv => ColorToString(kv.Value));
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex);
        }
    }

    private void PushStyle()
    {
        if (_syncingStyle) return;
        Interaction.StrokeColor = StrokeColor;
        Interaction.StrokeWidth = (float)StrokeWidth;
        Interaction.Fill = FillShapes;
        Interaction.BlurMode = BlurMode;
        Interaction.ApplyStyleToSelection();
    }

    /// <summary>Selecting an annotation shows its style in the properties bar.</summary>
    private void OnInteractionSelectionChanged()
    {
        UpdateToolUi();
        if (Interaction.Selected is not { } a) return;
        _syncingStyle = true;
        StrokeColor = a.Color;
        StrokeWidth = a.StrokeWidth;
        if (a.SupportsFill) FillShapes = a.Fill;
        if (a is BlurAnnotation blur) BlurMode = blur.Mode;
        _syncingStyle = false;
        Interaction.StrokeColor = a.Color;
        Interaction.StrokeWidth = a.StrokeWidth;
        if (a.SupportsFill) Interaction.Fill = a.Fill;
        if (a is BlurAnnotation b) Interaction.BlurMode = b.Mode;
    }

    partial void OnSelectedShotChanged(EditorDocument? oldValue, EditorDocument? newValue)
    {
        _thumbTimer.Stop();
        FlushThumbnail(); // before the old clip's pixels are released

        if (oldValue is not null)
        {
            oldValue.Commands.Changed -= OnCommandsChanged;
            oldValue.IsSelected = false;
        }
        if (newValue is not null)
        {
            newValue.Commands.Changed += OnCommandsChanged;
            newValue.IsSelected = true;
        }
        Interaction.Doc = newValue;
        RefreshUndoRedo();
    }

    // Film-strip thumbnail refresh, debounced so a burst of edits renders once.
    private readonly DispatcherQueueTimer _thumbTimer;
    private EditorDocument? _thumbPending;

    private void ScheduleThumbnail(EditorDocument shot)
    {
        if (_thumbPending is { } other && other != shot) other.RefreshThumbnail(); // switching clips: finish the old one now
        _thumbPending = shot;
        _thumbTimer.Stop();
        _thumbTimer.Start();
    }

    private void FlushThumbnail()
    {
        _thumbPending?.RefreshThumbnail();
        _thumbPending = null;
    }

    private void OnCommandsChanged()
    {
        if (SelectedShot is { } shot)
        {
            shot.Dirty = true; // edited since last save
            ScheduleThumbnail(shot);
        }
        _store.ScheduleSave();
        RefreshUndoRedo();
        Interaction.RaiseChanged();
    }

    private void RefreshUndoRedo()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        CleanCommand.NotifyCanExecuteChanged();
    }


    private bool CanClean() => SelectedShot is { } s && (s.Annotations.Count > 0 || s.Crop.Rect is not null);

    /// <summary>Restores the original capture by removing every annotation and the crop (undoable).</summary>
    [RelayCommand(CanExecute = nameof(CanClean))]
    private void Clean() => Interaction.ClearAll();

    private bool CanUndo() => SelectedShot?.Commands.CanUndo ?? false;
    private bool CanRedo() => SelectedShot?.Commands.CanRedo ?? false;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => SelectedShot?.Commands.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => SelectedShot?.Commands.Redo();

    public string Title => "ScreenshotApp Editor";
    /// <summary>With no clips the editor shows its start page (open a file or capture) instead of the tools.</summary>
    public bool HasShots => Shots.Count > 0;
    public bool IsEmpty => Shots.Count == 0;

    public EditorViewModel(SettingsService settings, ClipStore store, Func<Task<string?>> pickSavePath)
    {
        _settings = settings;
        _store = store;
        _pickSavePath = pickSavePath;
        store.Attach(() => Shots);
        _statusTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2.5);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => StatusText = "";
        Interaction.SelectionChanged += OnInteractionSelectionChanged;
        Shots.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasShots));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowFilmStrip));
        };
        Bin.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBin));
            OnPropertyChanged(nameof(BinCount));
            OnPropertyChanged(nameof(BinToolTip));
            OnPropertyChanged(nameof(ShowFilmStrip));
        };

        _widthSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _widthSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _widthSaveTimer.IsRepeating = false;
        _widthSaveTimer.Tick += (_, _) => SaveToolStyles();

        _thumbTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _thumbTimer.Interval = TimeSpan.FromMilliseconds(250);
        _thumbTimer.IsRepeating = false;
        _thumbTimer.Tick += (_, _) => FlushThumbnail();

        foreach (var tool in Enum.GetValues<ToolKind>().Where(t => t is not (ToolKind.Select or ToolKind.Crop)))
        {
            double width = settings.Current.ToolWidths.TryGetValue(tool.ToString(), out double saved)
                ? Math.Clamp(saved, 1, 40)
                : DefaultWidth(tool);
            _toolWidths[tool] = width;
            _toolColors[tool] = settings.Current.ToolColors.TryGetValue(tool.ToString(), out string? savedColor) &&
                                TryParseColor(savedColor, out var color)
                ? color
                : DefaultColor(tool);
        }
        OnToolChanged(Tool); // load the starting tool's width into the slider and interaction
    }

    /// <summary>Adds a shot at the front of the film strip, selects it and persists it.</summary>
    public EditorDocument AddShot(CapturedImage image)
    {
        var doc = new EditorDocument(image);
        Shots.Insert(0, doc);
        _store.Add(doc);
        SelectedShot = doc;
        return doc;
    }

    /// <summary>Fills the film strip with clips restored from disk (already newest first).</summary>
    public void Restore(IEnumerable<EditorDocument> clips)
    {
        foreach (var doc in clips) Shots.Add(doc);
        SelectedShot = Shots.FirstOrDefault();
    }

    /// <summary>Closes a clip: it leaves the film strip for the recycle bin (kept until the app exits).</summary>
    public void RemoveShot(EditorDocument doc)
    {
        int index = Shots.IndexOf(doc);
        if (index < 0) return;
        bool wasSelected = doc == SelectedShot;
        Shots.Remove(doc);
        MoveToBin(doc, index);

        if (Shots.Count == 0)
        {
            SelectedShot = null; // the editor stays open and shows its start page
            LastShotClosed?.Invoke(this, EventArgs.Empty);
        }
        else if (wasSelected || SelectedShot is null)
        {
            // Removing the selected item makes the ListView clear its selection; pick the neighbour.
            SelectedShot = Shots[Math.Min(index, Shots.Count - 1)];
        }
        doc.Unload(); // no longer shown: only the compressed copy waits in the bin
    }

    public void ShowStatus(string text)
    {
        StatusText = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // The toolbar and shortcuts act on the selected clip; the film-strip context menu calls the
    // *Shot* methods directly on any clip without selecting it first.

    [RelayCommand]
    public Task CopyAsync() => SelectedShot is { } shot ? CopyShotAsync(shot) : Task.CompletedTask;

    [RelayCommand]
    private Task SaveAsync() => SelectedShot is { } shot ? SaveShotAsync(shot) : Task.CompletedTask;

    [RelayCommand]
    private Task SaveAsAsync() => SelectedShot is { } shot ? SaveShotAsAsync(shot) : Task.CompletedTask;

    public async Task CopyShotAsync(EditorDocument shot)
    {
        try
        {
            var (flat, png) = await FlattenAsync(shot);
            await ClipboardService.CopyImageAsync(flat, png);
            ShowStatus("Copied to clipboard");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowStatus("Copy failed: " + ex.Message);
        }
        finally
        {
            ReleaseIfIdle(shot);
        }
    }

    public async Task SaveShotAsync(EditorDocument shot)
    {
        try
        {
            var (flat, png) = await FlattenAsync(shot);
            if (shot.SavedPath is null)
                shot.SavedPath = await ImageSaver.SaveAsync(flat, _settings.Current, png);
            else
                await ImageSaver.SaveAsync(flat, shot.SavedPath, IsJpg(shot.SavedPath), png);
            MarkSaved(shot);
            ShowStatus("Saved " + Path.GetFileName(shot.SavedPath));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowStatus("Save failed: " + ex.Message);
        }
        finally
        {
            ReleaseIfIdle(shot);
        }
    }

    public async Task SaveShotAsAsync(EditorDocument shot)
    {
        try
        {
            string? path = await _pickSavePath();
            if (path is null) return;
            var (flat, png) = await FlattenAsync(shot);
            await ImageSaver.SaveAsync(flat, path, IsJpg(path), png);
            shot.SavedPath = path;
            MarkSaved(shot);
            ShowStatus("Saved " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowStatus("Save failed: " + ex.Message);
        }
        finally
        {
            ReleaseIfIdle(shot);
        }
    }

    /// <summary>
    /// The clip as it is exported. One without edits is its capture as it stands, so that comes with the PNG the
    /// clip already holds, and neither the render nor a second PNG encode is needed.
    /// </summary>
    private static async Task<(CapturedImage Flat, byte[]? Png)> FlattenAsync(EditorDocument shot)
    {
        await shot.EnsureLoadedAsync();
        var image = shot.Image;
        if (shot.Annotations.Count == 0 && shot.Crop.Rect is null) return (image, await shot.GetPngAsync());
        return (ImageExporter.Render(image, shot.Annotations, shot.Crop.Rect), null);
    }

    /// <summary>A clip acted on from the context menu was loaded just for that; drop its pixels again.</summary>
    private void ReleaseIfIdle(EditorDocument shot)
    {
        if (shot != SelectedShot) shot.Unload();
    }

    /// <summary>Closes every clip (into the recycle bin); the editor stays open, empty.</summary>
    public void RemoveAllShots()
    {
        var all = Shots.ToList();
        Shots.Clear();
        SelectedShot = null; // the editor stays open and shows its start page
        // Last one first, so the bin lists them in film-strip order and restores them in place.
        for (int i = all.Count - 1; i >= 0; i--)
        {
            MoveToBin(all[i], i);
            all[i].Unload();
        }
        LastShotClosed?.Invoke(this, EventArgs.Empty);
    }

    // ---- recycle bin: closed clips stay recoverable for a while ----

    /// <summary>The bin keeps at most this many clips; closing another deletes the oldest for good.</summary>
    public const int BinCapacity = 20;

    /// <summary>A clip is deleted for good once it has been in the bin this long.</summary>
    public static readonly TimeSpan BinLifetime = TimeSpan.FromHours(24);

    /// <summary>Closed clips, most recently closed first. Not persisted: emptied at exit.</summary>
    public ObservableCollection<EditorDocument> Bin { get; } = new();

    /// <summary>Film-strip position each binned clip had (so a restore puts it back there) and when it was closed.</summary>
    private readonly Dictionary<EditorDocument, (int Index, DateTime ClosedUtc)> _binInfo = new();

    private DispatcherQueueTimer? _binExpiryTimer;

    public bool HasBin => Bin.Count > 0;
    public int BinCount => Bin.Count;
    public string BinToolTip => Bin.Count == 1 ? "1 closed clip" : $"{Bin.Count} closed clips";

    /// <summary>The film strip stays up while the bin has something to restore, even with no clips open.</summary>
    public bool ShowFilmStrip => HasShots || HasBin;

    private void MoveToBin(EditorDocument doc, int index)
    {
        _store.Remove(doc);
        _binInfo[doc] = (index, DateTime.UtcNow);
        Bin.Insert(0, doc);

        // Over capacity: the oldest closed clips (at the end) go for good.
        if (Bin.Count > BinCapacity) PurgeFromBin(Bin.Skip(BinCapacity).ToList());

        if (_binExpiryTimer is null)
        {
            _binExpiryTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _binExpiryTimer.Interval = TimeSpan.FromMinutes(1);
            _binExpiryTimer.Tick += (_, _) => PurgeExpired();
        }
        if (!_binExpiryTimer.IsRunning) _binExpiryTimer.Start();
    }

    /// <summary>Deletes clips that have been in the bin longer than <see cref="BinLifetime"/>.</summary>
    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - BinLifetime;
        var expired = Bin.Where(d => _binInfo[d].ClosedUtc <= cutoff).ToList();
        if (expired.Count > 0) PurgeFromBin(expired);
        if (Bin.Count == 0) _binExpiryTimer?.Stop();
    }

    private void PurgeFromBin(List<EditorDocument> docs)
    {
        _store.Purge(docs);
        foreach (var doc in docs)
        {
            Bin.Remove(doc);
            _binInfo.Remove(doc);
        }
    }

    /// <summary>Puts a closed clip back where it was in the film strip and opens it.</summary>
    public void RestoreShot(EditorDocument doc) => RestoreShot(doc, select: true);

    private void RestoreShot(EditorDocument doc, bool select)
    {
        if (!Bin.Remove(doc)) return;
        int index = _binInfo.Remove(doc, out var info) ? Math.Min(info.Index, Shots.Count) : 0;
        Shots.Insert(index, doc);
        _store.Restore(doc);
        if (select) SelectedShot = doc;
    }

    public void RestoreAllShots()
    {
        // Most recently closed first: the exact reverse of the closes, so every clip lands where it was.
        var docs = Bin.ToList();
        foreach (var doc in docs) RestoreShot(doc, select: false);
        SelectedShot ??= Shots.FirstOrDefault(); // keep the open clip if there is one
    }

    /// <summary>Deletes the binned clips for good.</summary>
    public void EmptyBin()
    {
        _store.Purge(Bin);
        Bin.Clear();
        _binInfo.Clear();
        _binExpiryTimer?.Stop();
    }

    /// <summary>Raised after the film strip becomes empty because clips were closed.</summary>
    public event EventHandler? LastShotClosed;

    /// <summary>A saved, unedited clip no longer needs to survive an app restart.</summary>
    private void MarkSaved(EditorDocument shot)
    {
        shot.Dirty = false;
        _store.ScheduleSave();
    }

    private static bool IsJpg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
}

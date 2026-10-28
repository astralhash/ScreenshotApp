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
    [NotifyPropertyChangedFor(nameof(Title))]
    private EditorDocument? _selectedShot;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _zoomText = "100%";

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

    [ObservableProperty] private ToolKind _tool = ToolKind.Arrow;
    [ObservableProperty] private Color _strokeColor = Palette[0];
    [ObservableProperty] private double _strokeWidth = DefaultWidth(ToolKind.Arrow);
    [ObservableProperty] private bool _fillShapes;

    [ObservableProperty] private BlurMode _blurMode = BlurMode.Pixelate;

    // What the properties bar shows depends on the active tool (or the selected shape).
    [ObservableProperty] private bool _isCropTool;
    [ObservableProperty] private bool _showStyle = true;
    [ObservableProperty] private bool _showFill = true;
    [ObservableProperty] private bool _showBlurMode;
    [ObservableProperty] private string _widthLabel = "Width";

    // Colour only matters for Solid redaction; strength only for Pixelate/Blur.
    [ObservableProperty] private bool _showColor = true;
    [ObservableProperty] private bool _showWidth = true;

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

    // Each drawing tool remembers its own width (persisted in settings). Select has none.
    private readonly Dictionary<ToolKind, double> _toolWidths = new();
    private readonly DispatcherQueueTimer _widthSaveTimer;

    private static double DefaultWidth(ToolKind tool) => tool == ToolKind.Highlighter ? 24 : 6;

    partial void OnToolChanged(ToolKind value)
    {
        Interaction.Tool = value;
        UpdateToolUi();
        if (!_toolWidths.TryGetValue(value, out double width)) return;

        // Switching tools must not restyle whatever shape is currently selected.
        _syncingStyle = true;
        StrokeWidth = width;
        _syncingStyle = false;
        Interaction.StrokeWidth = (float)width;
    }

    partial void OnStrokeColorChanged(Color value) => PushStyle();

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

    private void SaveToolWidths()
    {
        try
        {
            _settings.Current.ToolWidths = _toolWidths.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
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

    private void OnCommandsChanged()
    {
        if (SelectedShot is { } shot) shot.Dirty = true; // edited since last save
        _store.ScheduleSave();
        RefreshUndoRedo();
        OnPropertyChanged(nameof(Title)); // a crop changes the output size
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

    public string Title => SelectedShot is { } s ? $"Screenshot  ·  {s.OutputWidth} × {s.OutputHeight}" : "Screenshot";
    /// <summary>With no clips the editor shows its start page (open a file or capture) instead of the tools.</summary>
    public bool HasShots => Shots.Count > 0;
    public bool IsEmpty => Shots.Count == 0;
    public bool AutoCopyOnClose => _settings.Current.AutoCopyOnClose;

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
        };

        _widthSaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _widthSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _widthSaveTimer.IsRepeating = false;
        _widthSaveTimer.Tick += (_, _) => SaveToolWidths();

        foreach (var tool in Enum.GetValues<ToolKind>().Where(t => t is not (ToolKind.Select or ToolKind.Crop)))
        {
            double width = settings.Current.ToolWidths.TryGetValue(tool.ToString(), out double saved)
                ? Math.Clamp(saved, 1, 40)
                : DefaultWidth(tool);
            _toolWidths[tool] = width;
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

    /// <summary>Closes a clip: it leaves the film strip and is deleted from the store.</summary>
    public void RemoveShot(EditorDocument doc)
    {
        int index = Shots.IndexOf(doc);
        if (index < 0) return;
        bool wasSelected = doc == SelectedShot;
        Shots.Remove(doc);
        _store.Remove(doc);

        if (Shots.Count == 0)
        {
            SelectedShot = null; // the editor stays open and shows its start page
        }
        else if (wasSelected || SelectedShot is null)
        {
            // Removing the selected item makes the ListView clear its selection; pick the neighbour.
            SelectedShot = Shots[Math.Min(index, Shots.Count - 1)];
        }
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
            await shot.EnsureLoadedAsync();
            await ClipboardService.CopyImageAsync(ImageExporter.Render(shot.Image, shot.Annotations, shot.Crop.Rect));
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
            await shot.EnsureLoadedAsync();
            var flat = ImageExporter.Render(shot.Image, shot.Annotations, shot.Crop.Rect);
            if (shot.SavedPath is null)
                shot.SavedPath = await ImageSaver.SaveAsync(flat, _settings.Current);
            else
                await ImageSaver.SaveAsync(flat, shot.SavedPath, IsJpg(shot.SavedPath));
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
            await shot.EnsureLoadedAsync();
            await ImageSaver.SaveAsync(ImageExporter.Render(shot.Image, shot.Annotations, shot.Crop.Rect), path, IsJpg(path));
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

    /// <summary>A clip acted on from the context menu was loaded just for that; drop its pixels again.</summary>
    private void ReleaseIfIdle(EditorDocument shot)
    {
        if (shot != SelectedShot) shot.Unload();
    }

    /// <summary>Closes every clip (deleting them from the store); the editor stays open, empty.</summary>
    public void RemoveAllShots()
    {
        foreach (var doc in Shots.ToList()) _store.Remove(doc);
        Shots.Clear();
        SelectedShot = null; // the editor stays open and shows its start page
    }
    /// <summary>A saved, unedited clip no longer needs to survive an app restart.</summary>
    private void MarkSaved(EditorDocument shot)
    {
        shot.Dirty = false;
        _store.ScheduleSave();
    }

    private static bool IsJpg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
}

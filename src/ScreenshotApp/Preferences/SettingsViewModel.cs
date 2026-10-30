using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using ScreenshotApp.Services;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenshotApp.Preferences;

/// <summary>One line of the hotkey list: what it triggers and the key currently assigned (or being recorded).</summary>
internal sealed partial class HotkeyRow : ObservableObject
{
    public HotkeyRow(HotkeyAction action) => Action = action;

    public HotkeyAction Action { get; }
    public string Label => HotkeyActions.Label(Action);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text))] public partial string Assigned { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Text))] public partial bool Recording { get; set; }

    public string Text => Recording ? "Press the keys…" : Assigned.Length == 0 ? "Not set" : Assigned;
    public bool CanClear => Assigned.Length > 0;

    partial void OnAssignedChanged(string value) => OnPropertyChanged(nameof(CanClear));
}

/// <summary>Edits <see cref="AppSettings"/>; every change is written back and saved to %AppData% immediately.</summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private bool _loading = true;

    [ObservableProperty] public partial bool TargetEditor { get; set; }
    [ObservableProperty] public partial bool TargetClipboard { get; set; }
    [ObservableProperty] public partial bool TargetFile { get; set; }

    [ObservableProperty] public partial string SaveFolder { get; set; } = "";
    [ObservableProperty] public partial string FilenamePattern { get; set; } = "";
    [ObservableProperty] public partial int FormatIndex { get; set; }

    [ObservableProperty] public partial bool LaunchAtStartup { get; set; }
    [ObservableProperty] public partial bool ConfirmRemoveAll { get; set; }
    [ObservableProperty] public partial bool CloseEditorWhenEmpty { get; set; }
    [ObservableProperty] public partial bool DuckEditorForCapture { get; set; }
    [ObservableProperty] public partial bool CaptureCursor { get; set; }
    [ObservableProperty] public partial bool ShareToolbarOnHover { get; set; }

    /// <summary>Capture overlay style: 0 = darken + crosshair, 1 = crosshair only.</summary>
    [ObservableProperty] public partial int OverlayStyleIndex { get; set; }

    public IReadOnlyList<HotkeyRow> Hotkeys { get; } = HotkeyActions.All.Select(a => new HotkeyRow(a)).ToList();

    [ObservableProperty] public partial string HotkeyMessage { get; set; } = "";

    private HotkeyRow? _recordingRow;

    public bool IsRecording => _recordingRow is not null;

    public Visibility HasHotkeyMessage => HotkeyMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnHotkeyMessageChanged(string value) => OnPropertyChanged(nameof(HasHotkeyMessage));

    public SettingsViewModel(SettingsService service)
    {
        _service = service;
        var s = service.Current;
        LoadHotkeys();
        TargetEditor = s.CaptureTargets.HasFlag(CaptureTargets.Editor);
        TargetClipboard = s.CaptureTargets.HasFlag(CaptureTargets.Clipboard);
        TargetFile = s.CaptureTargets.HasFlag(CaptureTargets.File);
        SaveFolder = s.SaveFolder;
        FilenamePattern = s.FilenamePattern;
        FormatIndex = s.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        LaunchAtStartup = s.LaunchAtStartup;
        ConfirmRemoveAll = s.ConfirmRemoveAll;
        CloseEditorWhenEmpty = s.CloseEditorWhenEmpty;
        DuckEditorForCapture = s.DuckEditorForCapture;
        CaptureCursor = s.CaptureCursor;
        OverlayStyleIndex = s.DimOverlay ? 0 : 1;
        ShareToolbarOnHover = s.ShareToolbarOnHover;
        _loading = false;
    }

    private void LoadHotkeys()
    {
        foreach (var row in Hotkeys)
            row.Assigned = Hotkey.Parse(_service.Current.GetHotkey(row.Action))?.ToString() ?? "";
    }

    /// <summary>Starts listening for a key combination. Global hotkeys are released meanwhile, or they would swallow the keys.</summary>
    public void BeginRecording(HotkeyRow row)
    {
        EndRecording(apply: false);
        _recordingRow = row;
        row.Recording = true;
        HotkeyMessage = "Press the new key combination. Esc cancels.";
        App.SuspendHotkeys();
    }

    /// <summary>Stops listening and registers the hotkeys again.</summary>
    public void EndRecording(bool apply = true)
    {
        if (_recordingRow is null) return;
        _recordingRow.Recording = false;
        _recordingRow = null;
        if (apply) ApplyHotkeys();
    }

    /// <summary>Handles a key pressed while recording; returns false (and explains why) when it cannot be used.</summary>
    public bool TryAssignRecorded(HOT_KEY_MODIFIERS modifiers, uint key)
    {
        if (_recordingRow is not { } row) return false;
        if (Hotkey.Create(modifiers, key) is not { } hotkey)
        {
            HotkeyMessage = "That key needs Ctrl, Alt, Shift or Win with it (or use Print, Pause or an F key).";
            return false;
        }
        EndRecording(apply: false);
        Assign(row, hotkey.ToString());
        return true;
    }

    public void Clear(HotkeyRow row)
    {
        EndRecording(apply: false);
        Assign(row, "");
    }

    public void ResetHotkeys()
    {
        EndRecording(apply: false);
        _service.Current.Hotkeys.Clear();
        LoadHotkeys();
        SaveAndApplyHotkeys();
    }

    private void Assign(HotkeyRow row, string value)
    {
        // One key can only trigger one thing: take it away from whatever had it.
        if (value.Length > 0)
            foreach (var other in Hotkeys.Where(o => o != row && o.Assigned == value))
            {
                _service.Current.SetHotkey(other.Action, "");
                other.Assigned = "";
            }
        _service.Current.SetHotkey(row.Action, value);
        row.Assigned = value;
        SaveAndApplyHotkeys();
    }

    private void SaveAndApplyHotkeys()
    {
        try { _service.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        var failed = App.ApplyHotkeys();
        HotkeyMessage = failed.Count == 0
            ? ""
            : "Already used by another app, so it will not work: " + string.Join("; ", failed);
    }

    /// <summary>App version (without the git commit suffix the SDK appends), architecture and .NET runtime.</summary>
    public string VersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        var asm = typeof(SettingsViewModel).Assembly;
        string version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "?";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"Version {version} ({arch}) · .NET {Environment.Version}";
    }

    /// <summary>A capture would go nowhere.</summary>
    public bool NoTargets => !TargetEditor && !TargetClipboard && !TargetFile;

    /// <summary>What a saved file would be called right now, so the pattern can be checked at a glance.</summary>
    public string FilenamePreview
    {
        get
        {
            string name = ImageSaver.ExpandPattern(string.IsNullOrWhiteSpace(FilenamePattern) ? "Screenshot" : FilenamePattern, DateTime.Now);
            return "Example: " + name + (FormatIndex == 1 ? ".jpg" : ".png");
        }
    }

    partial void OnTargetEditorChanged(bool value) => Changed();
    partial void OnTargetClipboardChanged(bool value) => Changed();
    partial void OnTargetFileChanged(bool value) => Changed();
    partial void OnSaveFolderChanged(string value) => Changed();
    partial void OnFilenamePatternChanged(string value) => Changed();
    partial void OnFormatIndexChanged(int value) => Changed();
    partial void OnLaunchAtStartupChanged(bool value) => Changed();
    partial void OnConfirmRemoveAllChanged(bool value) => Changed();
    partial void OnCloseEditorWhenEmptyChanged(bool value) => Changed();
    partial void OnDuckEditorForCaptureChanged(bool value) => Changed();
    partial void OnCaptureCursorChanged(bool value) => Changed();
    partial void OnOverlayStyleIndexChanged(int value) => Changed();
    partial void OnShareToolbarOnHoverChanged(bool value) => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(NoTargets));
        OnPropertyChanged(nameof(FilenamePreview));
        if (_loading) return;

        var s = _service.Current;
        var targets = CaptureTargets.None;
        if (TargetEditor) targets |= CaptureTargets.Editor;
        if (TargetClipboard) targets |= CaptureTargets.Clipboard;
        if (TargetFile) targets |= CaptureTargets.File;
        s.CaptureTargets = targets;

        if (!string.IsNullOrWhiteSpace(SaveFolder)) s.SaveFolder = SaveFolder.Trim();
        if (!string.IsNullOrWhiteSpace(FilenamePattern)) s.FilenamePattern = FilenamePattern.Trim();
        s.ImageFormat = FormatIndex == 1 ? "jpg" : "png";
        s.ConfirmRemoveAll = ConfirmRemoveAll;
        s.CloseEditorWhenEmpty = CloseEditorWhenEmpty;
        s.DuckEditorForCapture = DuckEditorForCapture;
        s.CaptureCursor = CaptureCursor;
        s.DimOverlay = OverlayStyleIndex != 1;
        s.ShareToolbarOnHover = ShareToolbarOnHover;

        if (s.LaunchAtStartup != LaunchAtStartup)
        {
            s.LaunchAtStartup = LaunchAtStartup;
            StartupService.Apply(LaunchAtStartup);
        }

        try { _service.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
    }
}

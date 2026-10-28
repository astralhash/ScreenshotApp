using CommunityToolkit.Mvvm.ComponentModel;
using ScreenshotApp.Services;

namespace ScreenshotApp.Preferences;

/// <summary>Edits <see cref="AppSettings"/>; every change is written back and saved to %AppData% immediately.</summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _service;
    private bool _loading = true;

    [ObservableProperty] private bool _targetEditor;
    [ObservableProperty] private bool _targetClipboard;
    [ObservableProperty] private bool _targetFile;

    [ObservableProperty] private string _saveFolder = "";
    [ObservableProperty] private string _filenamePattern = "";
    [ObservableProperty] private int _formatIndex;

    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _autoCopyOnClose;
    [ObservableProperty] private bool _confirmRemoveAll;

    public SettingsViewModel(SettingsService service)
    {
        _service = service;
        var s = service.Current;
        TargetEditor = s.CaptureTargets.HasFlag(CaptureTargets.Editor);
        TargetClipboard = s.CaptureTargets.HasFlag(CaptureTargets.Clipboard);
        TargetFile = s.CaptureTargets.HasFlag(CaptureTargets.File);
        SaveFolder = s.SaveFolder;
        FilenamePattern = s.FilenamePattern;
        FormatIndex = s.ImageFormat.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        LaunchAtStartup = s.LaunchAtStartup;
        AutoCopyOnClose = s.AutoCopyOnClose;
        ConfirmRemoveAll = s.ConfirmRemoveAll;
        _loading = false;
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
    partial void OnAutoCopyOnCloseChanged(bool value) => Changed();
    partial void OnConfirmRemoveAllChanged(bool value) => Changed();

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
        s.AutoCopyOnClose = AutoCopyOnClose;
        s.ConfirmRemoveAll = ConfirmRemoveAll;

        if (s.LaunchAtStartup != LaunchAtStartup)
        {
            s.LaunchAtStartup = LaunchAtStartup;
            StartupService.Apply(LaunchAtStartup);
        }

        try { _service.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
    }
}

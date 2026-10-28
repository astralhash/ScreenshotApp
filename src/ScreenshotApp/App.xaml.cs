using Microsoft.UI.Xaml;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor;
using ScreenshotApp.Preferences;
using ScreenshotApp.Services;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenshotApp;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private MessageWindow? _window;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private readonly SettingsService _settings = new();
    private ClipStore? _clips;
    private EditorWindow? _editor;
    private SettingsWindow? _settingsWindow;

    internal static SettingsService Settings => ((App)Current)._settings;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        // Developer check of the redaction guarantees; opens no window and ignores the single-instance rule.
        if (Environment.GetCommandLineArgs().Contains("--selftest-redaction", StringComparer.OrdinalIgnoreCase))
        {
            try { RedactionSelfTest.Run(); }
            catch (Exception ex) { Log.Error(ex); }
            Exit();
            return;
        }
#endif

        _singleInstance = new Mutex(true, "ScreenshotApp.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Exit();
            return;
        }

        _settings.Load();

        _window = new MessageWindow();
        ClipboardService.Owner = _window.Handle;
        _tray = new TrayService(_window, OnTrayCommand);
        _hotkeys = new HotkeyService(_window);
        _clips = new ClipStore();

        // The editor window exists (hidden) from the start: WinUI exits when its last window closes, so this
        // keeps the app alive in the tray whatever other windows (like Settings) are opened and closed.
        GetEditor();
        if (_settings.Current.LaunchAtStartup) StartupService.Apply(true); // keep the Run entry pointing at this exe

        var ctrlShift = HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_SHIFT;
        var failed = new List<string>();
        if (!_hotkeys.Register(ctrlShift, VIRTUAL_KEY.VK_1, CaptureFullScreen)) failed.Add("Ctrl+Shift+1");
        if (!_hotkeys.Register(ctrlShift, VIRTUAL_KEY.VK_2, CaptureWindow)) failed.Add("Ctrl+Shift+2");
        if (!_hotkeys.Register(ctrlShift, VIRTUAL_KEY.VK_3, CaptureRegion)) failed.Add("Ctrl+Shift+3");
        if (!_hotkeys.Register(0, VIRTUAL_KEY.VK_SNAPSHOT, CaptureRegion)) failed.Add("Print Screen");

        if (failed.Count > 0)
            _tray.ShowBalloon("Hotkeys unavailable", "Already in use by another app: " + string.Join(", ", failed));

        await RestoreClipsAsync();

        // Optional switches, e.g. for a shortcut: ScreenshotApp.exe --open-editor / --open-settings
        // (An unpackaged app does not get its switches in LaunchActivatedEventArgs, so read the process command line.)
        var switches = Environment.GetCommandLineArgs();
        if (switches.Contains("--open-editor", StringComparer.OrdinalIgnoreCase)) OpenEditor();
        if (switches.Contains("--open-settings", StringComparer.OrdinalIgnoreCase)) OpenSettings();
    }

    /// <summary>Brings back unsaved clips from the previous run, silently; they are waiting in the editor when it is opened.</summary>
    private async Task RestoreClipsAsync()
    {
        try
        {
            var clips = await _clips!.LoadAsync();
            if (clips.Count == 0) return;
            GetEditor().Restore(clips);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenEditor: OpenEditor(); break;
            case TrayCommand.CaptureRegion: CaptureRegion(); break;
            case TrayCommand.CaptureWindow: CaptureWindow(); break;
            case TrayCommand.CaptureFullScreen: CaptureFullScreen(); break;
            case TrayCommand.Settings: OpenSettings(); break;
            case TrayCommand.Exit: Shutdown(); break;
        }
    }

    private EditorWindow GetEditor()
    {
        if (_editor is null)
        {
            _editor = new EditorWindow(_settings, _clips!);
            _editor.CaptureRequested += CaptureFromEditor;
            _editor.SettingsRequested += OpenSettings;
        }
        return _editor;
    }

    /// <summary>Opens the editor, with its clips or (when empty) its start page.</summary>
    private void OpenEditor() => GetEditor().ShowEditor();

    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Activate();
    }

    private void CaptureFullScreen() => TryCaptureFullScreen();

    private bool TryCaptureFullScreen()
    {
        try
        {
            HandleCapture(ScreenCapture.CaptureMonitorUnderCursor());
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _tray?.ShowBalloon("Capture failed", ex.Message);
            return false;
        }
    }

    private async void CaptureRegion() => await RunOverlayAsync(OverlayMode.Region);

    private async void CaptureWindow() => await RunOverlayAsync(OverlayMode.Window);

    /// <summary>Runs the selection overlay; true when something was captured (false when cancelled or it failed).</summary>
    private async Task<bool> RunOverlayAsync(OverlayMode mode)
    {
        if (CaptureOverlay.IsActive) return false;
        try
        {
            var image = await new CaptureOverlay(mode).RunAsync();
            if (image is null) return false;
            HandleCapture(image);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _tray?.ShowBalloon("Capture failed", ex.Message);
            return false;
        }
    }

    /// <summary>A capture started from the editor's start page: hide the editor so it is not in the shot.</summary>
    private async void CaptureFromEditor(CaptureKind kind)
    {
        var editor = GetEditor();
        editor.HideForCapture();
        await Task.Delay(250); // let the window actually leave the screen before the screen is frozen

        bool captured = false;
        try
        {
            captured = kind switch
            {
                CaptureKind.FullScreen => TryCaptureFullScreen(),
                CaptureKind.Window => await RunOverlayAsync(OverlayMode.Window),
                _ => await RunOverlayAsync(OverlayMode.Region),
            };
        }
        finally
        {
            // Cancelled, failed, or the capture is not set to open in the editor: bring the editor back.
            if (!captured || !_settings.Current.CaptureTargets.HasFlag(CaptureTargets.Editor))
                editor.ShowEditor();
        }
    }

    /// <summary>Sends a fresh capture to every configured target (default: editor and clipboard).</summary>
    private async void HandleCapture(CapturedImage image)
    {
        var targets = _settings.Current.CaptureTargets;
        try
        {
            // Editor first so it appears immediately; clipboard and file work follow.
            EditorDocument? doc = targets.HasFlag(CaptureTargets.Editor) ? GetEditor().AddShot(image) : null;

            if (targets.HasFlag(CaptureTargets.Clipboard))
            {
                try
                {
                    // Reuse the clip's compressed copy when there is one (saves a second encode).
                    await ClipboardService.CopyImageAsync(image, doc is null ? null : await doc.GetPngAsync());
                    if (_editor is not null && doc is not null) _editor.ViewModel.ShowStatus("Copied to clipboard");
                    else _tray?.ShowBalloon("Screenshot copied", "The capture is on the clipboard.");
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                    _tray?.ShowBalloon("Copy failed", ex.Message);
                }
            }

            if (targets.HasFlag(CaptureTargets.File))
            {
                string path = await ImageSaver.SaveAsync(image, _settings.Current);
                if (doc is not null)
                {
                    doc.SavedPath = path; // Ctrl+S in the editor then overwrites this file
                    _clips?.ScheduleSave();
                }
                if (doc is null) _tray?.ShowBalloon("Screenshot saved", path);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _tray?.ShowBalloon("Capture failed", ex.Message);
        }
    }

    private void Shutdown()
    {
        _clips?.Flush(); // persist unsaved clips and their edits
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _window?.Dispose();
        _singleInstance?.Dispose();
        Exit();
    }
}

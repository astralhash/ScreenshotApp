using Microsoft.UI.Xaml;
using ScreenshotApp.Capture;
using ScreenshotApp.Editor;
using ScreenshotApp.Preferences;
using ScreenshotApp.Services;
using ScreenshotApp.Sharing;

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
    private RegionShare? _share;

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
        if (Environment.GetCommandLineArgs().Contains("--selftest-share", StringComparer.OrdinalIgnoreCase))
        {
            try { await ShareSelfTest.RunAsync(); }
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

        var failed = RegisterHotkeys();
        if (failed.Count > 0)
            _tray.ShowBalloon("Hotkeys unavailable", "Already in use by another app: " + string.Join(", ", failed));

        await RestoreClipsAsync();

        // Optional switches, e.g. for a shortcut: ScreenshotApp.exe --open-editor / --open-settings
        // (An unpackaged app does not get its switches in LaunchActivatedEventArgs, so read the process command line.)
        var switches = Environment.GetCommandLineArgs();
        if (switches.Contains("--open-editor", StringComparer.OrdinalIgnoreCase)) OpenEditor();
        if (switches.Contains("--open-settings", StringComparer.OrdinalIgnoreCase)) OpenSettings();
#if DEBUG
        // Developer shortcut: --share-region=X,Y,W,H starts sharing that rectangle without the overlay.
        if (switches.FirstOrDefault(s => s.StartsWith("--share-region=", StringComparison.OrdinalIgnoreCase)) is { } share
            && share["--share-region=".Length..].Split(',') is [var x, var y, var w, var h])
            StartShare(new System.Drawing.Rectangle(int.Parse(x), int.Parse(y), int.Parse(w), int.Parse(h)));
#endif
    }

    /// <summary>(Re)registers every configured hotkey; returns the ones another app already owns, e.g. "Region: Print Screen".</summary>
    internal static List<string> ApplyHotkeys() => ((App)Current).RegisterHotkeys();

    /// <summary>Releases all hotkeys, so the Settings window can see the keys while one is being recorded.</summary>
    internal static void SuspendHotkeys() => ((App)Current)._hotkeys?.UnregisterAll();

    private List<string> RegisterHotkeys()
    {
        var failed = new List<string>();
        if (_hotkeys is null) return failed;
        _hotkeys.UnregisterAll();
        foreach (var action in HotkeyActions.All)
        {
            Action run = action switch
            {
                HotkeyAction.FullScreen => CaptureFullScreen,
                HotkeyAction.Window => CaptureWindow,
                HotkeyAction.Region => CaptureRegion,
                _ => CaptureRegionRepeat,
            };
            if (Hotkey.Parse(_settings.Current.GetHotkey(action)) is not { } hotkey) continue;
            if (!_hotkeys.Register(hotkey, run)) failed.Add($"{HotkeyActions.Label(action)}: {hotkey}");
        }
        return failed;
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
            case TrayCommand.ShareRegion: ShareRegion(); break;
            case TrayCommand.ChangeSharedRegion: ChangeSharedRegion(); break;
            case TrayCommand.StopSharing: StopSharing(); break;
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

    /// <summary>
    /// Runs a capture started from a hotkey or the tray. With <c>DuckEditorForCapture</c> a visible editor is hidden
    /// first (so it is not in the shot) and shown again afterwards, whether or not something was captured.
    /// </summary>
    private async Task<bool> RunDuckedAsync(Func<Task<bool>> capture)
    {
        var editor = _editor;
        bool ducked = editor is not null && _settings.Current.DuckEditorForCapture && editor.IsShown
                      && !CaptureOverlay.IsActive;
        if (ducked)
        {
            editor!.HideForCapture();
            await Task.Delay(150); // let the window leave the screen before it is frozen
        }
        try
        {
            return await capture();
        }
        finally
        {
            if (ducked) editor!.ShowEditor();
        }
    }

    private async void CaptureFullScreen() => await RunDuckedAsync(() => Task.FromResult(TryCaptureFullScreen()));

    private bool TryCaptureFullScreen() => TryCapture(ScreenCapture.CaptureMonitorUnderCursor);

    /// <summary>Captures the same screen rectangle as the last region capture, without the overlay.</summary>
    private async void CaptureRegionRepeat()
    {
        if (CaptureOverlay.IsActive) return;
        if (_settings.Current.LastRegion is { } last)
        {
            var area = new System.Drawing.Rectangle(last.X, last.Y, last.Width, last.Height);
            // Only when it still lies on the screen (a monitor may have been unplugged since).
            if (area.Width > 0 && area.Height > 0 && Monitors.VirtualScreen.Contains(area))
            {
                await RunDuckedAsync(() => Task.FromResult(TryCapture(() => ScreenCapture.CaptureRect(area))));
                return;
            }
        }
        CaptureRegion(); // nothing to repeat yet: pick a region now
    }

    private bool TryCapture(Func<CapturedImage> grab)
    {
        try
        {
            HandleCapture(grab());
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _tray?.ShowBalloon("Capture failed", ex.Message);
            return false;
        }
    }

    private async void CaptureRegion() => await RunDuckedAsync(() => RunOverlayAsync(OverlayMode.Region));

    private async void CaptureWindow() => await RunDuckedAsync(() => RunOverlayAsync(OverlayMode.Window));

    /// <summary>Runs the selection overlay; true when something was captured (false when cancelled or it failed).</summary>
    private async Task<bool> RunOverlayAsync(OverlayMode mode)
    {
        if (CaptureOverlay.IsActive) return false;
        try
        {
            var image = await new CaptureOverlay(mode).RunAsync();
            if (image is null) return false;
            if (mode == OverlayMode.Region) RememberRegion(image);
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

    private void RememberRegion(CapturedImage image)
    {
        _settings.Current.LastRegion = new RegionRect(image.ScreenX, image.ScreenY, image.Width, image.Height);
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error(ex); }
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

    /// <summary>
    /// Starts sharing a region: picks it with the region overlay, then mirrors it live in a hidden window that
    /// Teams (or any app that shares windows) lists in its window picker.
    /// </summary>
    private async void ShareRegion()
    {
        if (_share is not null || !RegionFrameSource.IsSupported)
        {
            if (_share is null) _tray?.ShowBalloon("Sharing unavailable", "Region sharing needs Windows 10 2004 or later.");
            return;
        }
        if (await PickShareRegionAsync() is { } region) StartShare(region);
    }

    private void StartShare(System.Drawing.Rectangle region)
    {
        try
        {
            var share = new RegionShare(region);
            share.Ended += StopSharing;
            _share = share;
            _tray!.Sharing = true;
            _tray.ShowBalloon("Region ready to share",
                $"In Teams, share a window and pick \"{MirrorWindow.WindowTitle}\". Stop it from this tray icon.");
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _tray?.ShowBalloon("Sharing failed", ex.Message);
        }
    }

    private async void ChangeSharedRegion()
    {
        if (_share is null) return;
        _share.ShowOutline(false); // not in the frozen screen the overlay shows
        var region = await PickShareRegionAsync();
        if (_share is null) return; // stopped meanwhile
        try
        {
            if (region is { } r) _share.ChangeRegion(r);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            _tray?.ShowBalloon("Sharing failed", ex.Message);
        }
        _share?.ShowOutline(true);
    }

    /// <summary>The selection overlay, used only for its rectangle; null when cancelled.</summary>
    private async Task<System.Drawing.Rectangle?> PickShareRegionAsync()
    {
        if (CaptureOverlay.IsActive) return null;
        try
        {
            var image = await new CaptureOverlay(OverlayMode.Region).RunAsync();
            return image is null ? null : new System.Drawing.Rectangle(image.ScreenX, image.ScreenY, image.Width, image.Height);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    private void StopSharing()
    {
        _share?.Dispose();
        _share = null;
        if (_tray is not null) _tray.Sharing = false;
    }

    private void Shutdown()
    {
        StopSharing();
        _clips?.Flush(); // persist unsaved clips and their edits
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _window?.Dispose();
        _singleInstance?.Dispose();
        Exit();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenshotApp.Services;

/// <summary>Where a fresh capture goes. Combinable; will be configurable on the settings page (phase 6).</summary>
[Flags]
internal enum CaptureTargets
{
    None = 0,
    Editor = 1,
    Clipboard = 2,
    File = 4,
}

internal sealed record RegionRect(int X, int Y, int Width, int Height);

internal sealed class AppSettings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    public string FilenamePattern { get; set; } = "Screenshot_yyyy-MM-dd_HH-mm-ss";
    public string ImageFormat { get; set; } = "png"; // png | jpg
    public bool LaunchAtStartup { get; set; }

    /// <summary>Draw the mouse cursor into captures.</summary>
    public bool CaptureCursor { get; set; }

    /// <summary>Ask before "Remove all clips" (the dialog has a "don't ask again" checkbox).</summary>
    public bool ConfirmRemoveAll { get; set; } = true;
    public bool CloseEditorWhenEmpty { get; set; }

    /// <summary>Hide the editor while a new clip is captured (so it is not in the shot), then bring it back.</summary>
    public bool DuckEditorForCapture { get; set; } = true;

    /// <summary>Darken the screen outside the selection in the capture overlay (off: crosshair only).</summary>
    public bool DimOverlay { get; set; } = true;

    /// <summary>Sharing region: hovering the red frame shows a toolbar (move, resize, end).</summary>
    public bool ShareToolbarOnHover { get; set; } = true;

    /// <summary>The one-time explanation of Share Region (a tray balloon) has been shown.</summary>
    public bool ShareRegionIntroShown { get; set; }

    /// <summary>Whether the region-capture zoom lens (Z) was on; remembered from capture to capture.</summary>
    public bool ZoomLensEnabled { get; set; }

    /// <summary>
    /// Hotkeys the user changed, per action name, as text ("Alt+Print"); "" for none.
    /// Actions that are missing use <see cref="HotkeyActions.DefaultHotkey"/>.
    /// </summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>The last region that was captured with the selection overlay (physical screen pixels), for "repeat the last region".</summary>
    public RegionRect? LastRegion { get; set; }

    public string GetHotkey(HotkeyAction action) =>
        Hotkeys.TryGetValue(action.ToString(), out var key) && (key == "" || Hotkey.Parse(key) is not null)
            ? key : HotkeyActions.DefaultHotkey(action); // unreadable saved text falls back to the default

    public void SetHotkey(HotkeyAction action, string value) => Hotkeys[action.ToString()] = value;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CaptureTargets CaptureTargets { get; set; } = CaptureTargets.Editor | CaptureTargets.Clipboard;

    /// <summary>Last stroke width per drawing tool, keyed by tool name. Missing tools use the defaults.</summary>
    public Dictionary<string, double> ToolWidths { get; set; } = new();

    /// <summary>Last colour per drawing tool as "#AARRGGBB", keyed by tool name. Missing tools use the defaults.</summary>
    public Dictionary<string, string> ToolColors { get; set; } = new();
}

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON in %AppData%\ScreenshotApp.</summary>
internal sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenshotApp", "settings.json");

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Current = new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, Options));
    }
}

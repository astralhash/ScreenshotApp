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

internal sealed class AppSettings
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    public string FilenamePattern { get; set; } = "Screenshot_yyyy-MM-dd_HH-mm-ss";
    public string ImageFormat { get; set; } = "png"; // png | jpg
    public bool LaunchAtStartup { get; set; }
    public bool AutoCopyOnClose { get; set; }

    /// <summary>Ask before "Remove all clips" (the dialog has a "don't ask again" checkbox).</summary>
    public bool ConfirmRemoveAll { get; set; } = true;

    /// <summary>Whether the region-capture zoom lens (Z) was on; remembered from capture to capture.</summary>
    public bool ZoomLensEnabled { get; set; }

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

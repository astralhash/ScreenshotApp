using Microsoft.UI.Windowing;

namespace ScreenshotApp.Services;

internal static class AppIcon
{
    /// <summary>Gives a window the app icon (taskbar, Alt+Tab, title bar). app.ico is copied next to the exe.</summary>
    public static void Apply(AppWindow window)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(path)) window.SetIcon(path);
    }
}

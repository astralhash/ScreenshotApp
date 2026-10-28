namespace ScreenshotApp.Services;

/// <summary>Appends unexpected errors to %AppData%\ScreenshotApp\log.txt.</summary>
internal static class Log
{
    public static void Info(string message) => Write(message);

    public static void Error(Exception ex) => Write(ex.ToString());

    private static void Write(string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenshotApp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), $"{DateTime.Now:s} {text}\n");
        }
        catch (IOException) { }
    }
}

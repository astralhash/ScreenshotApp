using System.Diagnostics;

namespace ScreenshotApp.Updater;

/// <summary>
/// Replaces a portable ScreenshotApp folder with a newer, already downloaded and checked copy, then restarts the app.
///
/// Started by ScreenshotApp (from a copy in %TEMP%, never from the folder it replaces) as:
///   ScreenshotApp.Updater.exe --pid N --source &lt;new files&gt; --target &lt;install folder&gt; --version X [--restart-arg A]...
///
/// 1. Waits for ScreenshotApp (pid N) to exit and its single-instance mutex to be free.
/// 2. Moves the app's files aside into &lt;target&gt;\.update-backup (only files of the new package or listed in the old
///    ScreenshotApp.files manifest; anything else the user keeps in that folder is left alone), copies the new files in.
/// 3. Starts the new ScreenshotApp.exe. If copying fails, or the new app exits without starting up, the backup is
///    put back and the old version is started again.
/// Everything is logged to %AppData%\ScreenshotApp\update-log.txt.
/// </summary>
internal static class Program
{
    private const string AppExe = "ScreenshotApp.exe";
    private const string Manifest = "ScreenshotApp.files";
    private const string MutexName = "ScreenshotApp.SingleInstance";
    private const string BackupFolder = ".update-backup";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Log("Unexpected error: " + ex);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        int pid = 0;
        string? source = null, target = null, version = null;
        var restartArgs = new List<string>();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pid": int.TryParse(args[i + 1], out pid); break;
                case "--source": source = Path.GetFullPath(args[i + 1]); break;
                case "--target": target = Path.GetFullPath(args[i + 1]); break;
                case "--version": version = args[i + 1]; break;
                case "--restart-arg": restartArgs.Add(args[i + 1]); break;
            }
        }
        Log($"Updating to {version}: {source} -> {target} (waiting for pid {pid})");

        if (source is null || target is null || !File.Exists(Path.Combine(source, AppExe)) || !File.Exists(Path.Combine(target, AppExe))
            || PathsOverlap(source, target))
        {
            Log("Invalid arguments; nothing changed.");
            return 1;
        }

        if (!WaitForAppExit(pid, TimeSpan.FromSeconds(30)))
        {
            Log("ScreenshotApp is still running; nothing changed.");
            return 1;
        }

        var newFiles = ListFiles(source);
        var oldFiles = ReadManifest(Path.Combine(target, Manifest));
        string backup = Path.Combine(target, BackupFolder);
        var moved = new List<string>();
        var copied = new List<string>();

        try
        {
            if (Directory.Exists(backup)) Retry(() => Directory.Delete(backup, recursive: true));
            foreach (string rel in newFiles.Concat(oldFiles).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string from = Path.Combine(target, rel);
                if (!File.Exists(from)) continue;
                string to = Path.Combine(backup, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Retry(() => File.Move(from, to));
                moved.Add(rel);
            }
            foreach (string rel in newFiles)
            {
                string to = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                Retry(() => File.Copy(Path.Combine(source, rel), to));
                copied.Add(rel);
            }
            Log($"Replaced {moved.Count} files with {copied.Count} new files.");
        }
        catch (Exception ex)
        {
            Log("Copying failed, restoring the previous version: " + ex.Message);
            Rollback(target, backup, moved, copied);
            StartApp(target, []);
            return 2;
        }

        if (!StartAndConfirm(target, restartArgs))
        {
            Log("The new version did not start; restoring the previous version.");
            Rollback(target, backup, moved, copied);
            StartApp(target, []);
            return 3;
        }

        try { Retry(() => Directory.Delete(backup, recursive: true)); }
        catch (Exception ex) { Log("Could not delete the backup: " + ex.Message); }
        RemoveEmptyFolders(target, moved);
        Log($"Update to {version} finished.");
        return 0;
    }

    private static bool PathsOverlap(string a, string b)
    {
        a = a.TrimEnd('\\') + "\\";
        b = b.TrimEnd('\\') + "\\";
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Waits for the old process to end and for its single-instance mutex to disappear.</summary>
    private static bool WaitForAppExit(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(timeout)) return false;
        }
        catch (ArgumentException) { } // already gone
        while (MutexExists())
        {
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(200);
        }
        return true;
    }

    private static bool MutexExists()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Starts the new version and waits until it holds its mutex (it is up) or exits early (it failed).</summary>
    private static bool StartAndConfirm(string target, IReadOnlyList<string> args)
    {
        using var process = StartApp(target, args);
        if (process is null) return false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (MutexExists()) return true;
            if (process.HasExited)
            {
                Log($"The new ScreenshotApp.exe exited with code {process.ExitCode} before starting up.");
                return false;
            }
            Thread.Sleep(200);
        }
        Log("The new version is still starting after 30 s; leaving it running.");
        return true;
    }

    private static Process? StartApp(string target, IReadOnlyList<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(target, AppExe)) { UseShellExecute = false, WorkingDirectory = target };
            foreach (string a in args) start.ArgumentList.Add(a);
            return Process.Start(start);
        }
        catch (Exception ex)
        {
            Log("Could not start ScreenshotApp: " + ex.Message);
            return null;
        }
    }

    private static void Rollback(string target, string backup, List<string> moved, List<string> copied)
    {
        foreach (string rel in copied)
            TryLog(() => Retry(() => File.Delete(Path.Combine(target, rel))));
        foreach (string rel in moved)
            TryLog(() => Retry(() => File.Move(Path.Combine(backup, rel), Path.Combine(target, rel), overwrite: true)));
        TryLog(() => Directory.Delete(backup, recursive: true));
        RemoveEmptyFolders(target, copied);
    }

    /// <summary>Relative paths of every file under <paramref name="root"/>.</summary>
    private static List<string> ListFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

    /// <summary>The old version's file list (one relative path per line); paths that leave the folder are ignored.</summary>
    private static List<string> ReadManifest(string path)
    {
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Select(l => l.Trim().Replace('/', '\\'))
            .Where(l => l.Length > 0 && !Path.IsPathRooted(l) && !l.Split('\\').Contains(".."))
            .ToList();
    }

    /// <summary>Deletes the folders of the given files (relative to root) that are now empty, deepest first.</summary>
    private static void RemoveEmptyFolders(string root, IEnumerable<string> files)
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in files)
            for (string? d = Path.GetDirectoryName(rel); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                dirs.Add(d);
        foreach (string dir in dirs.OrderByDescending(d => d.Length).Select(d => Path.Combine(root, d)))
            TryLog(() =>
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            });
    }

    /// <summary>File operations retried for a few seconds: antivirus scanners and Explorer briefly lock fresh files.</summary>
    private static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void TryLog(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log(ex.Message); }
    }

    private static void Log(string text)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenshotApp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "update-log.txt"), $"{DateTime.Now:s} {text}\n");
        }
        catch (IOException) { }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenshotApp.Services;

namespace ScreenshotApp.Updates;

/// <summary>A downloadable file of a release (the portable zip for this architecture).</summary>
internal sealed record ReleaseAsset(string Name, long Size, string Url, string? Sha256);

/// <summary>One published GitHub release.</summary>
internal sealed record ReleaseInfo(Version Version, string Tag, DateTimeOffset Published, string Notes, string PageUrl, ReleaseAsset? Asset);

/// <summary>The newest release plus every release between it and the running version (newest first), for the notes.</summary>
internal sealed record UpdateOffer(ReleaseInfo Latest, IReadOnlyList<ReleaseInfo> Newer);

/// <summary>
/// Looks for new releases on GitHub, downloads and checks the portable zip, and hands it to ScreenshotApp.Updater.exe.
/// Makes no network request unless asked to (the automatic check is off by default, see <c>AppSettings.CheckForUpdates</c>).
/// No WinUI types here.
/// </summary>
internal static class UpdateService
{
    public const string Repository = "astralhash/ScreenshotApp";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string ReleasesApi = "https://api.github.com/repos/" + Repository + "/releases?per_page=20";
    public const string UpdaterExe = "ScreenshotApp.Updater.exe";
    private const string AppExe = "ScreenshotApp.exe";
    private const string TempPrefix = "ScreenshotApp-update-";

    /// <summary>The running version, normalized to four parts (0.2.5 → 0.2.5.0) so comparisons are exact.</summary>
    /// <remarks>Declared before <see cref="Http"/>: static initializers run in order and the client's User-Agent uses it.</remarks>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient Http = CreateClient();

    public static string CurrentVersionText => Format(CurrentVersion);

    /// <summary>"0.2.5" (the fourth part only when it is set).</summary>
    public static string Format(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    /// <summary>"win-x64" or "win-arm64": the release zip that matches this process.</summary>
    private static string RuntimeId => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>
    /// Where the release list comes from. <c>--update-feed=&lt;file or URL&gt;</c> replaces the GitHub API, for testing
    /// with a local copy of the API response (its asset URLs may be local file paths too).
    /// </summary>
    private static string Feed =>
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--update-feed=", StringComparison.OrdinalIgnoreCase))
            is { } arg ? arg["--update-feed=".Length..] : ReleasesApi;

    private static HttpClient CreateClient()
    {
        // No overall timeout: the download can take a while. The API call uses its own.
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ScreenshotApp", CurrentVersionText));
        return client;
    }

    private static Version ReadCurrentVersion()
    {
        string text = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return ParseVersion(text) ?? new Version(0, 0, 0, 0);
    }

    /// <summary>"v0.2.6", "0.2.6", "0.2.6+abc", "0.2.6-beta" → 0.2.6.0; null when unreadable.</summary>
    public static Version? ParseVersion(string text)
    {
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) text = text[..cut];
        if (!Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    // ---------------------------------------------------------------- check

    /// <summary>
    /// Asks GitHub for the releases; returns the offer when one is newer than the running version, else null.
    /// Drafts and prereleases are ignored. Throws on network or format errors (the caller shows the message).
    /// </summary>
    public static async Task<UpdateOffer?> CheckAsync(CancellationToken cancel = default)
    {
        string json = await ReadFeedAsync(cancel);
        var releases = ParseReleases(json);
        var newer = releases.Where(r => r.Version > CurrentVersion).OrderByDescending(r => r.Version).ToList();
        return newer.Count == 0 ? null : new UpdateOffer(newer[0], newer);
    }

    private static async Task<string> ReadFeedAsync(CancellationToken cancel)
    {
        string feed = Feed;
        if (IsLocal(feed)) return await File.ReadAllTextAsync(LocalPath(feed), cancel);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, feed);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await Http.SendAsync(request, timeout.Token);
        if ((int)response.StatusCode is 403 or 429)
            throw new HttpRequestException("GitHub is limiting requests right now. Try again in an hour.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    private static List<ReleaseInfo> ParseReleases(string json)
    {
        var list = new List<ReleaseInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft") || Bool(r, "prerelease")) continue;
            string tag = Str(r, "tag_name") ?? "";
            if (ParseVersion(tag) is not { } version) continue;

            ReleaseAsset? asset = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    string name = Str(a, "name") ?? "";
                    if (!name.EndsWith($"-{RuntimeId}.zip", StringComparison.OrdinalIgnoreCase)) continue;
                    string? digest = Str(a, "digest");
                    string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest["sha256:".Length..] : null;
                    asset = new ReleaseAsset(name, a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        Str(a, "browser_download_url") ?? "", sha);
                    break;
                }

            DateTimeOffset.TryParse(Str(r, "published_at"), out var published);
            list.Add(new ReleaseInfo(version, tag, published, (Str(r, "body") ?? "").Replace("\r\n", "\n"),
                Str(r, "html_url") ?? ReleasesPage, asset));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ---------------------------------------------------------------- can it be installed here?

    /// <summary>Null when the update can be installed in place; otherwise why not (the dialog then offers the download page).</summary>
    public static string? WhyCannotInstall(ReleaseInfo release)
    {
#if DEBUG
        if (!Environment.GetCommandLineArgs().Any(a => a.StartsWith("--update-feed=", StringComparison.OrdinalIgnoreCase)))
            return "This is a development build, so it is not updated in place.";
#endif
        if (release.Asset is null)
            return $"This release has no download for {RuntimeId}.";
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, AppExe)))
            return "ScreenshotApp is not running from its own folder.";
        if (!IsFolderWritable(AppContext.BaseDirectory))
            return "ScreenshotApp's folder cannot be written to without administrator rights.";
        return null;
    }

    private static bool IsFolderWritable(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- download

    /// <summary>
    /// Downloads the release zip to %TEMP%\ScreenshotApp-update-&lt;version&gt;, checks its size and SHA-256, extracts
    /// it and checks its contents. Returns the folder that holds the new ScreenshotApp.exe. Throws with a readable
    /// message when something does not match; the download is deleted then.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken cancel)
    {
        var asset = release.Asset ?? throw new InvalidOperationException("This release has no download for this PC.");
        string work = Path.Combine(Path.GetTempPath(), TempPrefix + Format(release.Version));
        TryDeleteDirectory(work);
        Directory.CreateDirectory(work);
        try
        {
            string zip = Path.Combine(work, asset.Name);
            await DownloadFileAsync(asset, zip, progress, cancel);
            await VerifyAsync(asset, zip, cancel);

            string staged = Path.Combine(work, "staged");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged), cancel);
            File.Delete(zip);
            return CheckPackage(staged, release.Version);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }
    }

    private static async Task DownloadFileAsync(ReleaseAsset asset, string target, IProgress<double> progress, CancellationToken cancel)
    {
        if (IsLocal(asset.Url))
        {
            await using var src = File.OpenRead(LocalPath(asset.Url));
            await using var dst = File.Create(target);
            await CopyWithProgressAsync(src, dst, src.Length, progress, cancel);
            return;
        }

        using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        await using var file = File.Create(target);
        await CopyWithProgressAsync(stream, file, total, progress, cancel);
    }

    private static async Task CopyWithProgressAsync(Stream src, Stream dst, long total, IProgress<double> progress, CancellationToken cancel)
    {
        var buffer = new byte[81920];
        long done = 0;
        double reported = -1;
        int read;
        while ((read = await src.ReadAsync(buffer, cancel)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            double fraction = total > 0 ? Math.Min(1, (double)done / total) : 0;
            if (fraction - reported >= 0.005) { reported = fraction; progress.Report(fraction); }
        }
        progress.Report(1);
    }

    private static async Task VerifyAsync(ReleaseAsset asset, string zip, CancellationToken cancel)
    {
        long size = new FileInfo(zip).Length;
        if (asset.Size > 0 && size != asset.Size)
            throw new InvalidDataException($"The download is incomplete ({size:N0} of {asset.Size:N0} bytes).");
        if (asset.Sha256 is null)
        {
            Log.Info($"Update: {asset.Name} has no SHA-256 digest on GitHub; checked the size only.");
            return;
        }
        await using var stream = File.OpenRead(zip);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel));
        if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download does not match the checksum published on GitHub.");
    }

    /// <summary>The folder in the extracted zip that holds ScreenshotApp.exe (the zip may or may not have a top folder).</summary>
    private static string CheckPackage(string staged, Version expected)
    {
        string root = File.Exists(Path.Combine(staged, AppExe))
            ? staged
            : Directory.GetDirectories(staged).FirstOrDefault(d => File.Exists(Path.Combine(d, AppExe)))
              ?? throw new InvalidDataException("The download does not contain ScreenshotApp.exe.");
        if (!File.Exists(Path.Combine(root, UpdaterExe)))
            throw new InvalidDataException("The download does not contain the updater.");
        string? product = FileVersionInfo.GetVersionInfo(Path.Combine(root, AppExe)).ProductVersion;
        if (product is null || ParseVersion(product) != expected)
            throw new InvalidDataException($"The downloaded ScreenshotApp.exe is version {product ?? "?"}, not {Format(expected)}.");
        return root;
    }

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Starts ScreenshotApp.Updater.exe (copied out of the package first, so it never replaces itself). It waits for
    /// this process to exit, swaps the files and starts the new version. The caller then shuts the app down.
    /// </summary>
    public static void LaunchInstaller(string stagedRoot, Version version, bool openEditor)
    {
        string work = Path.GetDirectoryName(Path.GetFullPath(stagedRoot).TrimEnd('\\'))!;
        if (!Path.GetFileName(work).StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase))
            work = Path.GetDirectoryName(work)!; // the zip had a top folder: staged\<folder>
        string helper = Path.Combine(work, UpdaterExe);
        File.Copy(Path.Combine(stagedRoot, UpdaterExe), helper, overwrite: true);

        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = work };
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("--source");
        start.ArgumentList.Add(stagedRoot);
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(AppContext.BaseDirectory.TrimEnd('\\'));
        start.ArgumentList.Add("--version");
        start.ArgumentList.Add(Format(version));
        start.ArgumentList.Add("--restart-arg");
        start.ArgumentList.Add("--updated");
        if (openEditor)
        {
            start.ArgumentList.Add("--restart-arg");
            start.ArgumentList.Add("--open-editor");
        }
        Log.Info($"Update: starting the updater for {Format(version)}");
        using var _ = Process.Start(start) ?? throw new InvalidOperationException("The updater did not start.");
    }

    /// <summary>Deletes update folders left in %TEMP% by earlier updates (a minute after startup, when the updater has exited).</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), TempPrefix + "*"))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsLocal(string location) =>
        location.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathFullyQualified(location);

    private static string LocalPath(string location) =>
        location.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(location).LocalPath : location;
}

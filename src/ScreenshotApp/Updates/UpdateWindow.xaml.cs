using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenshotApp.Services;
using Windows.Graphics;
using Windows.Win32;
using WinRT.Interop;

namespace ScreenshotApp.Updates;

/// <summary>
/// Shows a new release with the notes of every release since the running one, and offers to download and install it.
/// Installing hands over to ScreenshotApp.Updater.exe and shuts the app down (see <see cref="UpdateService"/>).
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private readonly UpdateOffer _offer;
    private readonly string? _cannotInstall;
    private CancellationTokenSource? _download;

    /// <summary>"Skip this version" was chosen.</summary>
    internal event Action<Version>? Skipped;

    /// <summary>
    /// The update is downloaded and checked: start the updater (folder holding the new exe) and exit.
    /// Returns false (after logging) when the updater could not be started.
    /// </summary>
    internal Func<string, Version, bool>? Install { get; set; }

    /// <summary>A region share is running: installing ends it, so ask first.</summary>
    internal Func<bool>? IsSharing { get; set; }

    internal UpdateWindow(UpdateOffer offer)
    {
        _offer = offer;
        _cannotInstall = UpdateService.WhyCannotInstall(offer.Latest);
        InitializeComponent();

        Title = "Update available";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppIcon.Apply(AppWindow);
        Closed += (_, _) => _download?.Cancel();

        var latest = offer.Latest;
        HeadingText.Text = $"ScreenshotApp {UpdateService.Format(latest.Version)} is available";
        var details = new List<string> { $"You have {UpdateService.CurrentVersionText}" };
        if (latest.Published != default) details.Add("released " + latest.Published.ToLocalTime().ToString("d MMMM yyyy"));
        if (latest.Asset is { Size: > 0 } asset) details.Add($"{asset.Size / 1_000_000.0:0} MB download");
        DetailText.Text = string.Join(" · ", details);
        ReleaseNotes.Fill(NotesText, offer.Newer);

        if (_cannotInstall is not null)
        {
            UpdateButton.Content = "Open download page";
            ShowStatus(_cannotInstall + " Download the new version from GitHub instead.", error: false);
        }

        PlaceWindow();
    }

    /// <summary>560 x 640 DIPs, centered on the display under the cursor, capped to its work area.</summary>
    private void PlaceWindow()
    {
        PInvoke.GetCursorPos(out var pt);
        var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest).WorkArea;
        double scale = PInvoke.GetDpiForWindow(new(WindowNative.GetWindowHandle(this))) / 96.0;
        int w = Math.Min((int)(560 * scale), area.Width), h = Math.Min((int)(640 * scale), (int)(area.Height * 0.92));
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        StatusText.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_cannotInstall is not null)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(_offer.Latest.PageUrl));
            return;
        }
        if (IsSharing?.Invoke() == true && !await ConfirmAsync(
                "End the region share?", "Updating restarts ScreenshotApp, which ends the region share.", "Update"))
            return;

        await DownloadAndInstallAsync();
    }

    private async Task DownloadAndInstallAsync()
    {
        _download = new CancellationTokenSource();
        SetBusy(true);
        ShowStatus("Downloading…", error: false);
        Progress.Value = 0;
        Progress.IsIndeterminate = false;
        Progress.Visibility = Visibility.Visible;
        var release = _offer.Latest;
        try
        {
            var progress = new Progress<double>(f =>
            {
                Progress.Value = f;
                if (release.Asset is { Size: > 0 } a)
                    StatusText.Text = $"Downloading… {f * a.Size / 1_000_000.0:0} of {a.Size / 1_000_000.0:0} MB";
                if (f >= 1)
                {
                    StatusText.Text = "Checking the download…";
                    Progress.IsIndeterminate = true;
                }
            });
            string staged = await UpdateService.DownloadAsync(release, progress, _download.Token);

            ShowStatus("Restarting ScreenshotApp…", error: false);
            if (Install?.Invoke(staged, release.Version) == true) return; // the app is shutting down
            throw new InvalidOperationException("The updater could not be started.");
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            ShowStatus("Download cancelled.", error: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowStatus("The update failed: " + ex.Message, error: true);
            UpdateButton.Content = "Try again";
        }
        Progress.Visibility = Visibility.Collapsed;
        SetBusy(false);
        _download = null;
    }

    /// <summary>While downloading only Cancel (the Later button) is active.</summary>
    private void SetBusy(bool busy)
    {
        UpdateButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
        LaterButton.Content = busy ? "Cancel" : "Later";
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Skipped?.Invoke(_offer.Latest.Version);
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_download is not null) _download.Cancel();
        else Close();
    }

    private async void PageLink_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri(_offer.Latest.PageUrl));

    private async Task<bool> ConfirmAsync(string title, string text, string primary)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = text,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}

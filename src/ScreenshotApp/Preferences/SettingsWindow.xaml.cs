using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ScreenshotApp.Services;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.Win32;
using WinRT.Interop;

namespace ScreenshotApp.Preferences;

public sealed partial class SettingsWindow : Window
{
    internal SettingsViewModel ViewModel { get; }

    internal SettingsWindow(SettingsService settings)
    {
        ViewModel = new SettingsViewModel(settings);
        InitializeComponent();

        Title = "Settings";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppIcon.Apply(AppWindow);

        // 700 x 820 DIPs, centered on the display under the cursor, capped to its work area.
        PInvoke.GetCursorPos(out var pt);
        var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest).WorkArea;
        double scale = PInvoke.GetDpiForWindow(new(WindowNative.GetWindowHandle(this))) / 96.0;
        int w = Math.Min((int)(700 * scale), area.Width), h = Math.Min((int)(820 * scale), (int)(area.Height * 0.92));
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.SaveFolder = folder.Path;
    }
}

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ScreenshotApp.Services;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using WinRT.Interop;

namespace ScreenshotApp.Preferences;

public sealed partial class SettingsWindow : Window
{
    internal SettingsViewModel ViewModel { get; }

    internal SettingsWindow(SettingsService settings)
    {
        ViewModel = new SettingsViewModel(settings);
        InitializeComponent();

        Closed += (_, _) => ViewModel.EndRecording(); // never leave the global hotkeys released

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

    // ---- hotkey recording ----

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not HotkeyRow row) return;
        if (row.Recording) ViewModel.EndRecording();
        else ViewModel.BeginRecording(row);
    }

    private void Hotkey_LostFocus(object sender, RoutedEventArgs e) => ViewModel.EndRecording();

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is HotkeyRow row) ViewModel.Clear(row);
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e) => ViewModel.ResetHotkeys();

    // Tunnelling, so the focused button never sees the keys (Space/Enter would otherwise "click" it).
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsRecording) return;
        e.Handled = true;
        HandleRecordedKey(e.Key);
    }

    // Print Screen only produces a key-up when pressed alone.
    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsRecording || e.Key != VirtualKey.Snapshot) return;
        e.Handled = true;
        HandleRecordedKey(e.Key);
    }

    private void HandleRecordedKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Escape:
                ViewModel.EndRecording();
                return;
            case VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.LeftMenu or VirtualKey.RightMenu:
                return; // wait for the actual key
        }

        var modifiers = default(HOT_KEY_MODIFIERS);
        if (IsDown(VirtualKey.Control)) modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (IsDown(VirtualKey.Menu)) modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (IsDown(VirtualKey.Shift)) modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;

        ViewModel.TryAssignRecorded(modifiers, (uint)key);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.SaveFolder = folder.Path;
    }
}

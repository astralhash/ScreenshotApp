# Changelog

## 0.2.1

- Experimental: **Share Region…** (tray menu) mirrors a screen region live into a hidden "Shared region" window, so apps that can only share whole windows, like Microsoft Teams, can share just that region. A red frame marks the area; change or stop it from the tray. Turn it on with the new *Enable experimental features* setting under General.
- Window capture now grabs the clicked window itself: windows in front of it are no longer in the shot, and its rounded corners are filled from the screen behind it.
- Settings has an About section with the version and a link to the GitHub page.

## 0.2.0

- Moved from .NET 8 to .NET 10 (runtime 10.0.14). The portable release zip is self-contained, so nothing extra needs installing; building from source needs the .NET 10 SDK.
- Internal: view-model properties now use C# 14 partial properties (CommunityToolkit.Mvvm), which keeps the build warning-free and is the AOT-friendly form for WinUI.

## 0.1.4

- Hotkeys are configurable in Settings: click a hotkey and press the new keys. Global hotkeys are paused while you record one.
- New "repeat last region" capture (default Shift+Print Screen): it captures the previous region again without the overlay, and falls back to the overlay if there is no previous region or it is off-screen.
- New setting to include the mouse cursor in captures (off by default).
- New setting to hide the editor while capturing (on by default), in a new "Capture settings" section.
- New setting to hide the editor when its last clip is closed (off by default).
- Region capture: with the zoom lens on (Z), press C to copy the colour of the pixel under the cursor as #RRGGBB.
- Film strip: Left/Right select the adjacent clip, Delete closes the open clip, and the strip scrolls to keep the selected clip in view.
- Closing the editor no longer copies the current clip to the clipboard.
- Start page buttons tidied up.

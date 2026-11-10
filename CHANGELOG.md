# Changelog

## 0.3.1

- Editor: moving or resizing a shape is smoother, most of all on large captures. Only the shape you drag is redrawn while you drag it, instead of the whole image.
- Editor: film-strip thumbnails update with less work after each edit, and hovering over text labels is lighter.
- Capturing: the selection overlay comes up faster after the hotkey, and window captures finish faster.
- Copying to the clipboard uses less memory. Saving or copying a clip you have not edited, and the *File* capture target with PNG, no longer compress the image a second time.

## 0.3.0

- Updates: Settings → *Updates* can check GitHub for a new release once a day. This is off by default, and ScreenshotApp makes no internet connection unless you turn it on or click *Check now*. When there is a new version you get a notification and an *Update to …* item in the tray menu. The update window shows what changed in every release since yours, and *Update now* downloads the new version, checks it against GitHub's checksum, restarts ScreenshotApp and installs it. Your clips and settings are kept. *Skip this version* hides that release from the automatic check.
- This is the first version with the updater, so it has to be installed by hand once. Later versions can update themselves.

## 0.2.5

- Editor: closed clips go to a bin on the right edge of the film strip instead of being deleted. Click the bin to put a clip back where it was (or *Restore all*). The bin keeps the 20 most recently closed clips for 24 hours each, and is emptied when you exit ScreenshotApp; *Empty bin* clears it sooner. *Remove all clips* also goes to the bin.
- Editor: film-strip thumbnails now show your annotations and crop, and update shortly after each edit, undo or redo. Clips restored at startup show their edits too.

## 0.2.4

- Share Region has a frame rate limit (Settings → *Share region*, default 30 fps, 0 = unlimited). It applies right away, even during a share. At 30 fps it uses roughly 40% less CPU and 30% less GPU than unlimited, and meeting apps rarely send more than 30 fps anyway.
- Share Region no longer waits for the screen's refresh before showing each frame, so frames are not held up.
- Editor: drawing and dragging stay smooth with many annotations. Finished annotations and the image are cached instead of being redrawn on every mouse move, and redraws are limited to one per frame.

## 0.2.3

- Share Region: rest the cursor on the red frame and it turns into resize arrows, like a window edge. Drag an edge to change the width or height, or a corner to change both. A small toolbar appears where the cursor is, with a handle to move the region and an X to end sharing.
- Share Region tray menu: *Set Sharing Region* / *Remove Sharing Region* (the hotkey is shown beside it). *Change Shared Region…* is gone; use the frame instead. The first time you set a region, a short notice says to share the "Shared region" window.
- Hotkeys now say `Print` instead of `Print Screen` (for example `Ctrl+Print`). A saved hotkey that can no longer be read falls back to its default, so hotkeys you set to a Print Screen combination are reset.
- The tray menu shows each action's hotkey. A left click on the tray icon opens the editor; right-click opens the menu.

## 0.2.2

- Share Region is no longer experimental: the *Enable experimental features* setting is gone and it is always in the tray menu.
- New hotkey to start / stop Share Region (default `Ctrl+Shift+Print Screen`). The full screen default is now `Ctrl+Print Screen`.

## 0.2.1

- **Share Region…** (tray menu) mirrors a screen region live into a hidden "Shared region" window, so apps that can only share whole windows, like Microsoft Teams, can share just that region. A red frame marks the area; change or stop it from the tray.
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

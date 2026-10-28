# ScreenshotApp

A small, modern screenshot tool for Windows 11. It lives in the notification area: press a hotkey, capture the
full screen, a window or a region, and land in a clean editor to annotate, copy or save.

Built with C#, .NET 8 and WinUI 3 (Windows App SDK). It is unpackaged, so there is no installer and nothing to register.

## Features

**Capture**
- Full screen (the monitor under the cursor), a single window (hover to highlight, click to capture), or a region (drag).
- Frozen, dimmed overlay with a crosshair and a live width × height readout, on every monitor, per-monitor-v2 DPI aware.
- Press **Z** during a region capture for a zoom lens with a pixel-grid view, the cursor pixel's position and color;
  the **arrow keys** move the cursor one pixel at a time.
- Each capture can open in the editor, go to the clipboard, and/or be saved to a folder (configurable in Settings;
  editor and clipboard by default).

**Editor**
- One window with a film strip of clips along the bottom (newest on the left). Right-click a clip for
  Copy / Save / Save As / Close / Remove all, or close it with the × on its thumbnail.
- Unsaved clips and their edits are kept across restarts until you close them.
- Start page when there are no clips: open an image file, or take a capture. You can also drag PNG, JPEG or BMP files into the window.
- Tools: Select/Move, Arrow, Rectangle, Line, Pen, Highlighter, Text, Numbered step, Blur/Pixelate/Solid fill, Crop.
- Every annotation is an object: click to select, drag to move, handles to resize, Delete to remove.
- Undo / redo, a "Clean" button that restores the original capture, zoom and pan, Mica backdrop, light/dark following the system.
- Copy and Save flatten the image at full pixel resolution (rendered with Win2D, not screen-scaled).

## Hotkeys

| Keys | Action |
| --- | --- |
| `Ctrl+Shift+1` | Full screen (monitor under the cursor) |
| `Ctrl+Shift+2` | Window |
| `Ctrl+Shift+3` or `Print Screen` | Region |

If **Print Screen** does nothing, Windows may still hand it to the Snipping Tool: turn off
*Settings → Accessibility → Keyboard → Use the Print screen button to open screen capture*.
Hotkeys are shown in Settings but cannot be changed yet.

## Editor shortcuts

| Keys | Action |
| --- | --- |
| `V` `A` `R` `L` `P` `H` `T` `N` `B` `C` | Select, Arrow, Rectangle, Line, Pen, Highlighter, Text, Step, Blur, Crop |
| `Ctrl+C` / `Ctrl+S` / `Ctrl+Shift+S` | Copy / Save / Save As |
| `Ctrl+Z` / `Ctrl+Shift+Z` | Undo / redo |
| `Delete`, `Esc` | Remove the selected annotation, deselect |
| `Shift` while drawing | Straight lines (45° steps) and perfect squares. The highlighter is straight by default and Shift switches it to freehand |
| `Ctrl+wheel`, `Ctrl+0`, `Ctrl+1` | Zoom, fit to window, 100% |
| `Space`+drag or middle-drag | Pan |
| `Enter` / `Esc` (Crop tool) | Apply / cancel the crop |

## Requirements

- Windows 10 1809 or newer (built and tested on Windows 11), x64.
- To build: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- To run a normal build: the .NET 8 runtime. A self-contained publish needs nothing.

## Download

A ready-to-run portable build is attached to each [release](https://github.com/astralhash/ScreenshotApp/releases):
unzip `ScreenshotApp-<version>-win-x64.zip` anywhere and run `ScreenshotApp.exe`. It needs no installer and no
.NET install. The exe is not code-signed, so Windows SmartScreen may ask for confirmation the first time.

## Build and run

```powershell
dotnet build src/ScreenshotApp -p:Platform=x64
src\ScreenshotApp\bin\x64\Debug\net8.0-windows10.0.19041.0\ScreenshotApp.exe
```

Optional launch switches: `--open-editor` and `--open-settings` open that window at start.

The app is single-instance, and `dotnet build` cannot replace the exe while it is running, so exit it from the tray first.
To check that the code compiles without stopping a running copy, build to another folder with `-p:OutDir=<folder>`.

### Portable build

```powershell
dotnet publish src/ScreenshotApp -c Release -r win-x64 -p:Platform=x64 --self-contained true -o publish
```

This is how the release zip is made. Copy the whole output folder anywhere (about 225 MB unzipped) and run `ScreenshotApp.exe`; keep the folder together.
Settings and clips live in `%AppData%\ScreenshotApp`, not next to the exe.

## Settings

Open it from the tray menu or the gear button in the editor. Saved to `%AppData%\ScreenshotApp\settings.json` on every change:
what a capture does, default save folder, file name pattern (`yyyy` `MM` `dd` `HH` `mm` `ss` tokens),
image format (PNG or JPEG), launch at startup, copy on editor close, and whether "Remove all clips" asks first.

## Project layout

```
src/ScreenshotApp/
  Capture/       screen grabbing (GDI), monitor/window enumeration, the Win32 selection overlay and zoom lens
  Editor/        editor window, view model, film-strip clips and their on-disk store, tool/selection logic
  Editor/Model/  UI-independent annotation classes and the undo/redo command stack
  Preferences/   settings window and view model
  Services/      hidden message window, tray icon, hotkeys, settings, clipboard, image save/load, startup entry
```

All Win32 interop goes through [CsWin32](https://github.com/microsoft/CsWin32), the editor canvas and export through
[Win2D](https://github.com/microsoft/Win2D), and MVVM through the
[CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet).
`CLAUDE.md` holds the architecture notes and the feature checklist.

## Known limitations

- Multi-monitor and mixed-DPI setups have not been tested on real hardware yet.
- Capture uses a GDI snapshot, so content protected by the system (some video players) can come out black.
- Window capture picks top-level windows only.
- Blur and Pixelate act on the captured image, not on other annotations drawn under them.
- Hotkeys are fixed. ARM64 is configured in the project but has not been built.
- Out of scope for now: recording, OCR, scroll capture, uploads, history, pin-to-screen.

## License

[MIT](LICENSE)

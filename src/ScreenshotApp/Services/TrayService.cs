using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Services;

internal enum TrayCommand
{
    OpenEditor = 1,
    CaptureRegion,
    CaptureWindow,
    CaptureFullScreen,
    Settings,
    Exit,
}

/// <summary>Notification-area icon with a context menu and balloon notifications.</summary>
internal sealed unsafe class TrayService : IDisposable
{
    private const uint CallbackMessage = PInvoke.WM_USER + 1;
    private const uint IconId = 1;

    private readonly MessageWindow _window;
    private readonly Action<TrayCommand> _onCommand;
    private HICON _icon;
    private bool _added;

    public TrayService(MessageWindow window, Action<TrayCommand> onCommand)
    {
        _window = window;
        _onCommand = onCommand;
        _window.MessageReceived += OnMessage;

        _icon = CreateIcon();
        var data = NewData(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = "Screenshot";
        _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data);
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var data = NewData(NOTIFY_ICON_DATA_FLAGS.NIF_INFO);
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_NOSOUND;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
    }

    private NOTIFYICONDATAW NewData(NOTIFY_ICON_DATA_FLAGS flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
    };

    private void OnMessage(uint msg, WPARAM wParam, LPARAM lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return;
        handled = true;

        uint mouseMsg = (uint)(lParam.Value & 0xFFFF);
        if (mouseMsg == PInvoke.WM_RBUTTONUP || mouseMsg == PInvoke.WM_CONTEXTMENU)
            ShowMenu();
        else if (mouseMsg == PInvoke.WM_LBUTTONUP)
            _onCommand(TrayCommand.CaptureRegion);
    }

    private void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            Append(menu, TrayCommand.OpenEditor, "Open Editor");
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
            Append(menu, TrayCommand.CaptureRegion, "Capture Region");
            Append(menu, TrayCommand.CaptureWindow, "Capture Window");
            Append(menu, TrayCommand.CaptureFullScreen, "Capture Full Screen");
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
            Append(menu, TrayCommand.Settings, "Settings");
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
            Append(menu, TrayCommand.Exit, "Exit");

            PInvoke.GetCursorPos(out var pt);
            // Required so the menu dismisses when clicking elsewhere.
            PInvoke.SetForegroundWindow(_window.Handle);
            int cmd = PInvoke.TrackPopupMenuEx(
                menu,
                (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN),
                pt.X, pt.Y, _window.Handle, (TPMPARAMS*)null);
            PInvoke.PostMessage(_window.Handle, PInvoke.WM_NULL, 0, 0);

            if (cmd != 0) _onCommand((TrayCommand)cmd);
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private static void Append(HMENU menu, TrayCommand command, string text) =>
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)command, text);

    /// <summary>Draws a 32x32 viewfinder icon (blue rounded square, white corner brackets).</summary>
    private static HICON CreateIcon()
    {
        const int size = 32;
        var pixels = new uint[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (!InRoundedRect(x + 0.5f, y + 0.5f, size, 7f)) continue;
            uint color = 0xFF0078D4; // ARGB
            if (IsBracket(x, y)) color = 0xFFFFFFFF;
            pixels[y * size + x] = color;
        }

        fixed (uint* p = pixels)
        {
            var color = PInvoke.CreateBitmap(size, size, 1, 32, p);
            var mask = PInvoke.CreateBitmap(size, size, 1, 1, null);
            var info = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            var icon = PInvoke.CreateIconIndirect(info);
            PInvoke.DeleteObject(color);
            PInvoke.DeleteObject(mask);
            return icon;
        }
    }

    private static bool InRoundedRect(float x, float y, int size, float r)
    {
        float cx = Math.Clamp(x, r, size - r), cy = Math.Clamp(y, r, size - r);
        float dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    private static bool IsBracket(int x, int y)
    {
        // Four L-shaped corners inside a 8..23 box, 2px thick, 5px long.
        const int lo = 8, hi = 23, t = 2, len = 5;
        bool nearX = x < lo + t || x > hi - t, nearY = y < lo + t || y > hi - t;
        bool inBox = x >= lo && x <= hi && y >= lo && y <= hi;
        if (!inBox) return false;
        bool xEnd = x < lo + len || x > hi - len, yEnd = y < lo + len || y > hi - len;
        return (nearX && yEnd) || (nearY && xEnd);
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        if (_added)
        {
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, NewData(0));
            _added = false;
        }
        if (_icon != HICON.Null)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = HICON.Null;
        }
    }
}

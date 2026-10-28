using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Services;

internal sealed class HotkeyService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    /// <summary>Registers a global hotkey. Returns false if another app already owns it.</summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        int id = _nextId++;
        if (!PInvoke.RegisterHotKey(_window.Handle, id, hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, hotkey.Key))
            return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys)
            PInvoke.UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
    }

    private void OnMessage(uint msg, WPARAM wParam, LPARAM lParam, ref bool handled)
    {
        if (msg == PInvoke.WM_HOTKEY && _actions.TryGetValue((int)wParam.Value, out var action))
        {
            action();
            handled = true;
        }
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        UnregisterAll();
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ScreenshotApp.Services;

/// <summary>Hidden message-only Win32 window that receives hotkey and tray messages.</summary>
internal sealed unsafe class MessageWindow : IDisposable
{
    private const string ClassName = "ScreenshotApp.MessageWindow";

    private static MessageWindow? s_instance; // one window per process; the wndproc is a static function pointer
    private bool _classRegistered;

    public HWND Handle { get; private set; }

    /// <summary>Raised for every message; set <c>handled</c> to true to stop default processing.</summary>
    public event MessageHandler? MessageReceived;

    public delegate void MessageHandler(uint msg, WPARAM wParam, LPARAM lParam, ref bool handled);

    public MessageWindow()
    {
        s_instance = this;
        var hInstance = PInvoke.GetModuleHandle((PCWSTR)null);

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &StaticWndProc,
                hInstance = (HINSTANCE)hInstance.Value,
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed.");
            _classRegistered = true;

            Handle = PInvoke.CreateWindowEx(
                0, className, className, 0, 0, 0, 0, 0,
                HWND.HWND_MESSAGE, HMENU.Null, (HINSTANCE)hInstance.Value, null);
        }

        if (Handle == HWND.Null)
            throw new InvalidOperationException("CreateWindowEx failed.");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static LRESULT StaticWndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam) =>
        s_instance is { } w ? w.WndProc(hwnd, msg, wParam, lParam) : PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        bool handled = false;
        MessageReceived?.Invoke(msg, wParam, lParam, ref handled);
        return handled ? (LRESULT)0 : PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != HWND.Null)
        {
            PInvoke.DestroyWindow(Handle);
            Handle = HWND.Null;
        }
        if (_classRegistered)
        {
            fixed (char* className = ClassName)
                PInvoke.UnregisterClass(className, (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value);
            _classRegistered = false;
        }
    }
}

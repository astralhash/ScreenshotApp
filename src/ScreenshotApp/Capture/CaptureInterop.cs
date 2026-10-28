using System.Runtime.InteropServices;
using ScreenshotApp.Services;
using Windows.Graphics.Capture;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace ScreenshotApp.Capture;

/// <summary>Windows.Graphics.Capture pieces that the C# projection does not expose.</summary>
internal static class CaptureInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");

    // IGraphicsCaptureItemInterop: IUnknown (3 slots), then CreateForWindow, CreateForMonitor.
    public static unsafe GraphicsCaptureItem CreateForWindow(HWND hwnd) => CreateItem(3, (nint)hwnd.Value);

    public static unsafe GraphicsCaptureItem CreateForMonitor(HMONITOR monitor) => CreateItem(4, (nint)monitor.Value);

    /// <summary>GraphicsCaptureItem.CreateForWindow/-Monitor are only reachable through IGraphicsCaptureItemInterop.</summary>
    private static unsafe GraphicsCaptureItem CreateItem(int slot, nint handle)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in IID_IGraphicsCaptureItemInterop, out var interop));
        try
        {
            var create = (delegate* unmanaged[Stdcall]<IntPtr, nint, Guid*, IntPtr*, int>)(*(void***)interop)[slot];
            var iid = IID_IGraphicsCaptureItem;
            IntPtr itemPtr;
            Marshal.ThrowExceptionForHR(create(interop, handle, &iid, &itemPtr));
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    /// <summary>
    /// Hides the yellow "being captured" border (Windows 11; IsBorderRequired is not in this SDK projection).
    /// Best effort: older systems lack the interface and the call may be refused.
    /// </summary>
    public static unsafe void TryDisableBorder(GraphicsCaptureSession session)
    {
        var ptr = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(ptr, in IID_IGraphicsCaptureSession3, out var session3) < 0) return;
        try
        {
            // IInspectable (6 slots), then get_IsBorderRequired, put_IsBorderRequired.
            var putIsBorderRequired = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)(*(void***)session3)[7];
            int hr = putIsBorderRequired(session3, 0);
            if (hr < 0) Log.Info($"Capture border could not be disabled (0x{hr:X8}).");
        }
        finally
        {
            Marshal.Release(session3);
        }
    }
}

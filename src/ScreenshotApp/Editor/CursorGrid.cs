using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace ScreenshotApp.Editor;

/// <summary>Grid that can change its mouse cursor (UIElement.ProtectedCursor is protected).</summary>
internal sealed class CursorGrid : Grid
{
    private InputSystemCursorShape? _shape;

    public void SetCursor(InputSystemCursorShape shape)
    {
        if (_shape == shape) return; // pointer-move calls this constantly
        _shape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}

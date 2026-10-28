namespace ScreenshotApp.Capture;

/// <summary>Top-down, premultiplied-opaque BGRA8 pixels plus where they came from (physical screen pixels).</summary>
internal sealed record CapturedImage(byte[] Pixels, int Width, int Height, int ScreenX, int ScreenY);

using Avalonia;

namespace QrOverlayScanner.Models;

public sealed record QrDetection(string Text, PixelRect PixelBounds)
{
    // Auto-confirm is only active when exactly one QR code is visible.
    // Text is therefore a stable identity and is unaffected by subpixel position jitter.
    public string TrackingKey => Text;
}

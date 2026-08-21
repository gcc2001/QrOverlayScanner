using Avalonia;
using QrOverlayScanner.Models;

namespace QrOverlayScanner.Services;

public interface IScreenCapture
{
    string BackendName { get; }

    /// <summary>
    /// True when the capture path may include pixels drawn by this overlay window.
    /// The scanner temporarily suppresses its scan line and candidate controls before capture.
    /// </summary>
    bool RequiresOverlaySuppression { get; }

    ScreenFrame Capture(PixelRect screenRegion);
}

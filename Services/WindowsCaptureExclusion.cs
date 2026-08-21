using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace QrOverlayScanner.Services;

internal static class WindowsCaptureExclusion
{
    // Available from Windows 10, version 2004.
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static void TryApply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return;

        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor == "HWND")
        {
            // Best effort. GDI SRCCOPY without CAPTUREBLT is already the primary fallback.
            _ = SetWindowDisplayAffinity(handle.Handle, WdaExcludeFromCapture);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);
}

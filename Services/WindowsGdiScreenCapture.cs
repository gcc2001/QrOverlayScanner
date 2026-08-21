using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using QrOverlayScanner.Models;

namespace QrOverlayScanner.Services;

/// <summary>
/// Windows-only screen capture. The DIB is top-down and stores pixels as BGRA32.
/// SRCCOPY is intentionally used without CAPTUREBLT so layered overlay windows are
/// not included by the GDI capture path.
/// </summary>
public sealed class WindowsGdiScreenCapture : IScreenCapture
{
    public string BackendName => "Windows GDI";

    public bool RequiresOverlaySuppression => false;
    private const uint Srccopy = 0x00CC0020;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    public ScreenFrame Capture(PixelRect screenRegion)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows GDI screen capture is only available on Windows.");

        if (screenRegion.Width <= 0 || screenRegion.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(screenRegion));

        nint screenDc = 0;
        nint memoryDc = 0;
        nint bitmap = 0;
        nint oldBitmap = 0;

        try
        {
            screenDc = GetDC(0);
            if (screenDc == 0)
                ThrowLastWin32Error("GetDC failed.");

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
                ThrowLastWin32Error("CreateCompatibleDC failed.");

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = screenRegion.Width,
                    // Negative height creates a top-down DIB, matching screen coordinates.
                    Height = -screenRegion.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = checked((uint)(screenRegion.Width * screenRegion.Height * 4))
                }
            };

            bitmap = CreateDIBSection(
                screenDc,
                ref bitmapInfo,
                DibRgbColors,
                out var pixelPointer,
                0,
                0);

            if (bitmap == 0 || pixelPointer == 0)
                ThrowLastWin32Error("CreateDIBSection failed.");

            oldBitmap = SelectObject(memoryDc, bitmap);
            if (oldBitmap == 0 || oldBitmap == new nint(-1))
                ThrowLastWin32Error("SelectObject failed.");

            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    screenRegion.Width,
                    screenRegion.Height,
                    screenDc,
                    screenRegion.X,
                    screenRegion.Y,
                    Srccopy))
            {
                ThrowLastWin32Error("BitBlt failed.");
            }

            GdiFlush();

            var pixels = new byte[checked(screenRegion.Width * screenRegion.Height * 4)];
            Marshal.Copy(pixelPointer, pixels, 0, pixels.Length);
            return new ScreenFrame(pixels, screenRegion.Width, screenRegion.Height);
        }
        finally
        {
            if (oldBitmap != 0 && memoryDc != 0)
                SelectObject(memoryDc, oldBitmap);

            if (bitmap != 0)
                DeleteObject(bitmap);

            if (memoryDc != 0)
                DeleteDC(memoryDc);

            if (screenDc != 0)
                ReleaseDC(0, screenDc);
        }
    }

    private static void ThrowLastWin32Error(string message) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDC(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(nint windowHandle, nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint deviceContext, nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        nint destinationDc,
        int xDestination,
        int yDestination,
        int width,
        int height,
        nint sourceDc,
        int xSource,
        int ySource,
        uint rasterOperation);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();
}

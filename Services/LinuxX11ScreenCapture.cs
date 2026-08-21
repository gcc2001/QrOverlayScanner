using System.Runtime.InteropServices;
using Avalonia;
using QrOverlayScanner.Models;

namespace QrOverlayScanner.Services;

/// <summary>
/// Linux/X11 screen capture implemented with XGetImage on the root window.
/// The result is converted to BGRA32 for ZXing.Net.
/// </summary>
public sealed class LinuxX11ScreenCapture : IScreenCapture
{
    private const int ZPixmap = 2;
    private const int LsbFirst = 0;

    public string BackendName => "Linux X11 / XGetImage";

    // Do not toggle Avalonia overlay opacity around every capture on X11.
    // Xfwm and other compositors publish opacity changes asynchronously, which can
    // make the visible lock controls flash and can feed a stale composited frame
    // back into the decoder. The scanner now stops capturing as soon as candidates
    // are locked, so only the thin animated scan line can be present while searching.
    public bool RequiresOverlaySuppression => false;

    public static bool TryProbe(out string error)
    {
        error = string.Empty;

        if (!OperatingSystem.IsLinux())
        {
            error = "当前操作系统不是 Linux。";
            return false;
        }

        try
        {
            var display = XOpenDisplay(null);
            if (display == 0)
            {
                error = "XOpenDisplay 返回空指针，请检查 DISPLAY 和 X11 权限。";
                return false;
            }

            XCloseDisplay(display);
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "未找到 libX11.so.6，请安装 libx11-6（Debian/Ubuntu）或 libX11（Fedora）。";
            return false;
        }
        catch (EntryPointNotFoundException exception)
        {
            error = $"libX11 缺少必要入口点：{exception.Message}";
            return false;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public ScreenFrame Capture(PixelRect screenRegion)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Linux X11 screen capture is only available on Linux.");

        if (screenRegion.Width <= 0 || screenRegion.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(screenRegion));

        var output = CreateOpaqueBlackFrame(screenRegion.Width, screenRegion.Height);
        nint display = 0;
        nint imagePointer = 0;

        try
        {
            display = XOpenDisplay(null);
            if (display == 0)
                throw new InvalidOperationException("XOpenDisplay 失败，请检查 DISPLAY 和 X11 访问权限。");

            var screen = XDefaultScreen(display);
            var root = XRootWindow(display, screen);
            var rootWidth = XDisplayWidth(display, screen);
            var rootHeight = XDisplayHeight(display, screen);

            var captureLeft = Math.Max(0, screenRegion.X);
            var captureTop = Math.Max(0, screenRegion.Y);
            var captureRight = Math.Min(rootWidth, checked(screenRegion.X + screenRegion.Width));
            var captureBottom = Math.Min(rootHeight, checked(screenRegion.Y + screenRegion.Height));
            var captureWidth = captureRight - captureLeft;
            var captureHeight = captureBottom - captureTop;

            if (captureWidth <= 0 || captureHeight <= 0)
                return new ScreenFrame(output, screenRegion.Width, screenRegion.Height);

            imagePointer = XGetImage(
                display,
                root,
                captureLeft,
                captureTop,
                checked((uint)captureWidth),
                checked((uint)captureHeight),
                nuint.MaxValue,
                ZPixmap);

            if (imagePointer == 0)
                throw new InvalidOperationException("XGetImage 失败；扫描区域可能不在当前 X11 根窗口内。");

            var image = Marshal.PtrToStructure<XImage>(imagePointer);
            ValidateImage(image, captureWidth, captureHeight);

            var destinationOffsetX = captureLeft - screenRegion.X;
            var destinationOffsetY = captureTop - screenRegion.Y;
            ConvertToBgra32(
                image,
                output,
                screenRegion.Width,
                destinationOffsetX,
                destinationOffsetY,
                captureWidth,
                captureHeight);

            return new ScreenFrame(output, screenRegion.Width, screenRegion.Height);
        }
        finally
        {
            if (imagePointer != 0)
                XDestroyImage(imagePointer);

            if (display != 0)
                XCloseDisplay(display);
        }
    }

    private static byte[] CreateOpaqueBlackFrame(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        return pixels;
    }

    private static void ValidateImage(XImage image, int expectedWidth, int expectedHeight)
    {
        if (image.Data == 0)
            throw new InvalidOperationException("XGetImage 返回了空像素缓冲区。");

        if (image.Width < expectedWidth || image.Height < expectedHeight)
            throw new InvalidOperationException("XGetImage 返回的图像尺寸小于请求尺寸。");

        if (image.BitsPerPixel is not (16 or 24 or 32))
        {
            throw new NotSupportedException(
                $"暂不支持 X11 的 {image.BitsPerPixel} bits-per-pixel 像素格式。");
        }

        if (image.BytesPerLine <= 0 || image.RedMask == 0 || image.GreenMask == 0 || image.BlueMask == 0)
            throw new NotSupportedException("X11 返回了无法识别的 TrueColor 像素格式。");
    }

    private static void ConvertToBgra32(
        XImage image,
        byte[] destination,
        int destinationWidth,
        int destinationOffsetX,
        int destinationOffsetY,
        int width,
        int height)
    {
        var bytesPerPixel = (image.BitsPerPixel + 7) / 8;
        var sourceLength = checked(image.BytesPerLine * height);
        var source = new byte[sourceLength];
        Marshal.Copy(image.Data, source, 0, sourceLength);

        var redMask = unchecked((ulong)image.RedMask);
        var greenMask = unchecked((ulong)image.GreenMask);
        var blueMask = unchecked((ulong)image.BlueMask);

        var redShift = CountTrailingZeros(redMask);
        var greenShift = CountTrailingZeros(greenMask);
        var blueShift = CountTrailingZeros(blueMask);
        var redMaximum = redMask >> redShift;
        var greenMaximum = greenMask >> greenShift;
        var blueMaximum = blueMask >> blueShift;

        for (var y = 0; y < height; y++)
        {
            var sourceRow = checked(y * image.BytesPerLine);
            var destinationRow = checked((destinationOffsetY + y) * destinationWidth * 4);

            for (var x = 0; x < width; x++)
            {
                var sourcePixel = checked(sourceRow + x * bytesPerPixel);
                var raw = ReadPixel(source, sourcePixel, bytesPerPixel, image.ByteOrder);
                var destinationIndex = checked(destinationRow + (destinationOffsetX + x) * 4);

                destination[destinationIndex] = ScaleComponent(raw, blueMask, blueShift, blueMaximum);
                destination[destinationIndex + 1] = ScaleComponent(raw, greenMask, greenShift, greenMaximum);
                destination[destinationIndex + 2] = ScaleComponent(raw, redMask, redShift, redMaximum);
                destination[destinationIndex + 3] = 255;
            }
        }
    }

    private static ulong ReadPixel(byte[] source, int offset, int byteCount, int byteOrder)
    {
        ulong value = 0;

        if (byteOrder == LsbFirst)
        {
            for (var index = 0; index < byteCount; index++)
                value |= (ulong)source[offset + index] << (index * 8);
        }
        else
        {
            for (var index = 0; index < byteCount; index++)
                value = (value << 8) | source[offset + index];
        }

        return value;
    }

    private static byte ScaleComponent(ulong pixel, ulong mask, int shift, ulong maximum)
    {
        if (maximum == 0)
            return 0;

        var component = (pixel & mask) >> shift;
        return checked((byte)((component * 255UL + maximum / 2UL) / maximum));
    }

    private static int CountTrailingZeros(ulong value)
    {
        var count = 0;
        while ((value & 1UL) == 0)
        {
            value >>= 1;
            count++;
        }
        return count;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public nint Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public nuint RedMask;
        public nuint GreenMask;
        public nuint BlueMask;
        public nint ObData;
        public XImageFunctions Functions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XImageFunctions
    {
        public nint CreateImage;
        public nint DestroyImage;
        public nint GetPixel;
        public nint PutPixel;
        public nint SubImage;
        public nint AddPixel;
    }

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    private static extern int XDefaultScreen(nint display);

    [DllImport("libX11.so.6")]
    private static extern nuint XRootWindow(nint display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayWidth(nint display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern int XDisplayHeight(nint display, int screenNumber);

    [DllImport("libX11.so.6")]
    private static extern nint XGetImage(
        nint display,
        nuint drawable,
        int x,
        int y,
        uint width,
        uint height,
        nuint planeMask,
        int format);

    [DllImport("libX11.so.6")]
    private static extern int XDestroyImage(nint image);
}

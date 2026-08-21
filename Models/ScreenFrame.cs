namespace QrOverlayScanner.Models;

public sealed record ScreenFrame(byte[] BgraPixels, int Width, int Height);

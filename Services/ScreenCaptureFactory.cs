namespace QrOverlayScanner.Services;

public sealed record ScreenCaptureSelection(
    IScreenCapture? Capture,
    string Message)
{
    public bool IsSupported => Capture is not null;
}

public static class ScreenCaptureFactory
{
    private const string ForceX11Variable = "QR_OVERLAY_FORCE_X11";

    public static ScreenCaptureSelection Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ScreenCaptureSelection(
                new WindowsGdiScreenCapture(),
                "Windows GDI");
        }

        if (OperatingSystem.IsLinux())
            return CreateLinuxCapture();

        return new ScreenCaptureSelection(
            null,
            "当前版本仅实现 Windows 和 Linux/X11 屏幕捕获。");
    }

    private static ScreenCaptureSelection CreateLinuxCapture()
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var forceX11 = string.Equals(
            Environment.GetEnvironmentVariable(ForceX11Variable),
            "1",
            StringComparison.Ordinal);

        var isWaylandSession =
            string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(waylandDisplay);

        if (isWaylandSession && !forceX11)
        {
            return new ScreenCaptureSelection(
                null,
                "检测到 Wayland 会话。连续桌面捕获需要 xdg-desktop-portal ScreenCast + PipeWire；" +
                "请切换到 X11 会话。仅用于测试 XWayland 时可设置 QR_OVERLAY_FORCE_X11=1。 ");
        }

        if (string.IsNullOrWhiteSpace(display))
        {
            return new ScreenCaptureSelection(
                null,
                "未检测到 DISPLAY，无法连接 Linux X11 显示服务器。");
        }

        if (!LinuxX11ScreenCapture.TryProbe(out var error))
        {
            return new ScreenCaptureSelection(
                null,
                $"Linux X11 捕获不可用：{error}");
        }

        var suffix = isWaylandSession
            ? "（强制 XWayland 模式，仅保证捕获 X11/XWayland 内容）"
            : string.Empty;

        return new ScreenCaptureSelection(
            new LinuxX11ScreenCapture(),
            $"Linux X11{suffix}");
    }
}

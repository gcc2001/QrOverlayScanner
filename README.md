# QrOverlayScanner

Avalonia 12.1 / .NET 10 的 Windows + Linux 桌面二维码区域扫描示例。

> [!TIP] 
> 本文档和代码示例均由AI完成，本人仅对功能进行验证测试

## 平台支持

| 平台 | 捕获后端 | 状态 |
|---|---|---|
| Windows 10/11 | GDI `BitBlt` | 完整支持 |
| Linux X11 会话 | Xlib `XGetImage` | 完整支持 |
| Linux Wayland 会话 | xdg-desktop-portal ScreenCast + PipeWire | 尚未集成；启动时给出明确提示 |
| Linux 上的 XWayland | Xlib `XGetImage` | 可通过环境变量强制试验，只保证 X11/XWayland 内容 |

Avalonia 在 Linux 默认使用 X11 后端；原生 Wayland 后端从 Avalonia 12.1 起仍是显式启用的实验能力。即使 Avalonia 程序通过 XWayland 运行，X11 客户端也不能可靠读取其他原生 Wayland 应用的桌面像素，因此项目默认拒绝在 Wayland 会话中启动自动扫描。

## 运行

### Windows

```powershell
dotnet restore
dotnet run
```

### Debian / Ubuntu，X11 会话

```bash
sudo apt install libx11-6 libice6 libsm6 libfontconfig1
dotnet restore
dotnet run
```

### Fedora，X11 会话

```bash
sudo dnf install libX11 libICE libSM fontconfig
dotnet restore
dotnet run
```

查看当前桌面会话：

```bash
echo "$XDG_SESSION_TYPE"
```

预期输出为：

```text
x11
```

在 Wayland 会话中仅用于验证 XWayland 的试验模式：

```bash
QR_OVERLAY_FORCE_X11=1 dotnet run
```

该模式不能保证捕获 GNOME、KDE 或其他原生 Wayland 窗口。

## 发布

```bash
# Windows x64
dotnet publish -c Release -r win-x64 --self-contained false

# Linux x64
dotnet publish -c Release -r linux-x64 --self-contained false

# Linux arm64
dotnet publish -c Release -r linux-arm64 --self-contained false
```

## 实现结构

```text
Services/
├── IScreenCapture.cs
├── ScreenCaptureFactory.cs
├── WindowsGdiScreenCapture.cs
├── WindowsCaptureExclusion.cs
├── LinuxX11ScreenCapture.cs
└── QrDecoder.cs
```

`ScreenCaptureFactory` 根据操作系统和 Linux 会话类型选择捕获后端。二维码识别层不依赖具体平台，所有后端统一返回 BGRA32 像素。

Linux/X11 后端通过 `XGetImage` 读取根窗口，再根据 `XImage` 的位深、字节序和 RGB mask 转换成 BGRA32。Linux/X11 不再在每次捕获前切换覆盖控件的透明度：XFCE/Xfwm 等合成器异步发布窗口透明度变化，持续切换会造成锁定框闪烁，并可能让解码器读取到上一帧的覆盖层。

## 扫描行为

- 主窗口包含按钮和结果文本框。
- 扫描窗口使用 5 DIP 自绘边框和自绘标题栏，并提供最大化/还原按钮。
- 界面采用接近微信“扫一扫”的暗色遮罩、中央透明取景框、绿色四角标记、柔和扫描线和底部状态栏。
- 每 200ms 只捕获中央透明取景框对应的桌面内容，并使用 ZXing.Net 识别多个二维码；外围暗色遮罩不会进入截图。
- 只有成功完整解码、且推算出的二维码完整边界全部位于扫描区域内时才显示锁定框。
- 候选集合连续两帧一致后进入锁定状态，并停止周期性屏幕捕获。
- 单个二维码锁定后，由独立倒计时器在 3 秒后返回已解码结果；可点击勾按钮提前确认。
- 多个二维码锁定后停止自动倒计时，必须点击目标二维码的勾按钮。
- 移动窗口、缩放窗口或调整取景框尺寸都会解除当前锁定；布局稳定 300ms 后恢复扫描。
- 只能扫描到部分二维码时，不锁定，也不触发自动识别。


## 扫描窗口视觉设计

视觉层只修改 Avalonia XAML 和候选框绘制，不改动 Windows GDI、Linux/X11 捕获后端以及二维码锁定状态机。

- 窗口外框仍为 5 DIP，但改为低对比深色圆角边框；
- 顶部使用紧凑的深色自绘标题栏和圆形关闭按钮；
- 中央扫描区域保持完全透明，外侧使用半透明黑色遮罩；
- 扫描区域采用微信绿色风格的四角括号和渐变扫描光束；
- 默认窗口尺寸由 780×580 DIP 增大为 900×700 DIP；
- 扫描区域不再设置 420 DIP 上限，而是按当前可用空间动态计算，并始终保持正方形；
- 取景框默认使用可用空间的 90%，可在 50%–100% 之间调节；
- 底部提供“− / + / 最大”控制，鼠标滚轮也可直接调整取景框；
- 标题栏新增最大化/还原按钮，最大化窗口后扫描区域会同步扩大；
- 锁定二维码后隐藏扫描线，候选框改为半透明绿色高亮；
- 确认按钮改为圆形绿色勾选按钮；
- 在 Linux/X11 上仍然只截取中央透明区域，因此外围遮罩不会降低二维码对比度，也不会重新引入 XFCE 闪烁问题。

该界面仅参考移动端扫码产品的布局语言和交互层级，没有复制微信资源、图标文件或品牌素材。


## 大尺寸二维码与可调取景框

为避免大二维码受固定 420 DIP 取景框限制，当前版本将扫描区域改为“窗口可用空间 × 用户比例”：

```text
最终取景框边长 = min(可用宽度, 可用高度) × 取景框比例
```

取景框比例默认是 90%，允许范围为 50%–100%，每次通过按钮或滚轮调整 10%。“最大”按钮会让取景框占满扫描内容区；标题栏方框按钮会在普通窗口和最大化窗口之间切换。

状态栏和取景框缩放按钮位于独立布局行，不在 `ScanViewport` 内。Windows GDI 和 Linux/X11 后端仍然只捕获 `ScanViewport` 对应的屏幕矩形，因此扩大取景框不会把底部文字或按钮捕获进去。

调整取景框时会递增扫描代次编号。尚未完成的旧截图即使稍后返回，也不会再覆盖新取景框对应的识别状态。

## Debian XFCE 闪烁修复

旧实现中，Linux 后端每 200ms 将扫描线、锁定框和勾选按钮的 `Opacity` 设为 0，等待约 20ms 后截图，再恢复为 1。Xfwm 的合成刷新与 Avalonia 渲染提交并不同步，因此屏幕上会看到覆盖层闪烁，截图也可能交替包含旧覆盖层和底层二维码。检测结果随之在“发现/未发现”之间切换，3 秒计时会被持续重置。

当前实现采用以下状态机：

1. 扫描阶段不再切换 Linux 覆盖层透明度；
2. 同一候选集合连续出现两帧后才锁定；
3. 锁定后停止 `_scanTimer`，不再反复截取带有绿色框和勾选按钮的画面；
4. 单码由独立 `_autoConfirmTimer` 完成 3 秒倒计时；
5. 窗口位置或大小改变时清除锁定，并在 300ms 防抖后恢复扫描。

该修改同时避免“已经识别但只能手动点击勾选”的问题。

## Wayland 后续实现

Wayland 的完整实现不能直接复用 `XGetImage`。需要：

1. 使用 `org.freedesktop.portal.ScreenCast` 创建授权会话；
2. 由用户选择要共享的显示器；
3. 从 portal 获得 PipeWire node id；
4. 持续读取 PipeWire 视频帧；
5. 根据 portal 返回的逻辑坐标、缩放和显示器位置裁剪扫描窗口覆盖区域；
6. 在窗口关闭时停止并释放 portal session。

这会在首次使用时显示系统级屏幕共享授权 UI，属于 Wayland 的安全模型要求，不应绕过。

## 验证建议

重点测试：

- Windows 100%、125%、150%、200% DPI；
- Linux X11 单显示器和 XRandR 多显示器；
- GNOME Xorg、KDE Plasma X11；
- 二维码贴近扫描窗口边缘；
- 同时存在多个二维码；
- 视频、浏览器和 GPU 加速窗口；
- 远程桌面和虚拟机环境中的透明窗口支持。

当前容器没有可用的图形会话，因此无法进行真实桌面捕获测试；项目文件已完成 XML、结构和本机 X11 ABI 布局检查。

## Avalonia 12 cursor compatibility

For the bottom-right resize grip, use Avalonia's cursor name:

```xml
Cursor="BottomRightCorner"
```

Do not use `SizeNorthwestSoutheast`; it is not a member of Avalonia's `StandardCursorType` and causes `Cursor.Parse` to throw during XAML loading.

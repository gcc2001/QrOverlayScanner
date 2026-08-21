using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QrOverlayScanner.Models;
using QrOverlayScanner.Services;

namespace QrOverlayScanner.Views;

public partial class ScannerWindow : Window
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan AutoConfirmDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GeometryResumeDelay = TimeSpan.FromMilliseconds(300);
    private const int RequiredStableFrames = 2;
    private const double MinViewportScale = 0.50;
    private const double MaxViewportScale = 1.00;
    private const double ViewportScaleStep = 0.10;

    private readonly IScreenCapture? _screenCapture;
    private readonly string _captureMessage;
    private readonly QrDecoder _qrDecoder = new();
    private readonly DispatcherTimer _scanTimer;
    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _autoConfirmTimer;
    private readonly DispatcherTimer _geometryResumeTimer;
    private readonly Stopwatch _autoConfirmWatch = new();

    private bool _scanInProgress;
    private bool _closingWithResult;
    private bool _candidatesLocked;
    private double _scanLineProgress;
    private double _scanLineDirection = 1;
    private string? _autoCandidateKey;
    private string? _pendingCandidateSignature;
    private int _pendingCandidateFrames;
    private int _scanGeneration;
    private double _viewportScale = 0.90;
    private IReadOnlyList<QrDetection> _lockedDetections = Array.Empty<QrDetection>();

    private Grid ScanViewportContainer => this.FindControl<Grid>("ScanViewportHost")!;
    private Grid ScanArea => this.FindControl<Grid>("ScanViewport")!;
    private Border ScanLineControl => this.FindControl<Border>("ScanLine")!;
    private Canvas LockLayer => this.FindControl<Canvas>("LockBoxLayer")!;
    private Canvas ConfirmLayer => this.FindControl<Canvas>("ConfirmButtonLayer")!;
    private TextBlock StatusLabel => this.FindControl<TextBlock>("StatusText")!;
    private TextBlock ViewportScaleLabel => this.FindControl<TextBlock>("ViewportScaleText")!;

    public ScannerWindow()
    {
        InitializeComponent();

        var captureSelection = ScreenCaptureFactory.Create();
        _screenCapture = captureSelection.Capture;
        _captureMessage = captureSelection.Message;

        _scanTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.UIThread)
        {
            Interval = ScanInterval
        };
        _scanTimer.Tick += ScanTimer_OnTick;

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher.UIThread)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationTimer.Tick += AnimationTimer_OnTick;

        _autoConfirmTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.UIThread)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _autoConfirmTimer.Tick += AutoConfirmTimer_OnTick;

        _geometryResumeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.UIThread)
        {
            Interval = GeometryResumeDelay
        };
        _geometryResumeTimer.Tick += GeometryResumeTimer_OnTick;

        Opened += OnOpened;
        Closed += OnClosed;
        PositionChanged += (_, _) => ScannerGeometry_OnChanged();
        Resized += (_, _) =>
        {
            UpdateScanViewportSize();
            ScannerGeometry_OnChanged();
        };
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        UpdateScanViewportSize();

        if (_screenCapture is null)
        {
            StatusLabel.Text = _captureMessage;
            return;
        }

        if (OperatingSystem.IsWindows())
            WindowsCaptureExclusion.TryApply(this);

        StatusLabel.Text = $"{_captureMessage} · 将取景框覆盖完整二维码";
        _animationTimer.Start();
        _scanTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closingWithResult = true;
        StopAllTimers();
    }

    private async void ScanTimer_OnTick(object? sender, EventArgs e)
    {
        if (_scanInProgress || _closingWithResult || _candidatesLocked ||
            !IsVisible || _screenCapture is null)
        {
            return;
        }

        var region = GetScanSurfaceScreenRegion();
        if (region.Width < 40 || region.Height < 40)
            return;

        var generation = _scanGeneration;
        _scanInProgress = true;
        try
        {
            var detections = await CaptureAndDecodeAsync(_screenCapture, region);

            if (!_closingWithResult && IsVisible && !_candidatesLocked &&
                generation == _scanGeneration)
            {
                UpdateDetections(detections);
            }
        }
        catch (Exception exception)
        {
            if (generation != _scanGeneration)
                return;

            ResetPendingCandidate();
            ResetAutoCandidate();
            ClearCandidateVisuals();
            StatusLabel.Text = $"扫描失败：{exception.Message}";
        }
        finally
        {
            _scanInProgress = false;
        }
    }

    private async Task<IReadOnlyList<QrDetection>> CaptureAndDecodeAsync(
        IScreenCapture capture,
        PixelRect region)
    {
        var suppressOverlay = capture.RequiresOverlaySuppression;
        if (suppressOverlay)
        {
            SetCaptureVisualsVisible(false);

            // A render turn only guarantees that Avalonia submitted a new frame.
            // Some X11 compositors publish that frame later, so Linux/X11 no longer
            // uses this suppression path. It is retained for capture backends that
            // can synchronously honor it.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            await Task.Delay(20);
        }

        try
        {
            return await Task.Run(() =>
            {
                var frame = capture.Capture(region);
                return _qrDecoder.Decode(frame);
            });
        }
        finally
        {
            if (suppressOverlay && !_closingWithResult)
                SetCaptureVisualsVisible(true);
        }
    }

    private void SetCaptureVisualsVisible(bool visible)
    {
        var opacity = visible ? 1d : 0d;
        ScanLineControl.Opacity = opacity;
        LockLayer.Opacity = opacity;
        ConfirmLayer.Opacity = opacity;
    }

    private void UpdateScanViewportSize()
    {
        // The toolbar occupies a separate layout row, so the viewport may use almost
        // the entire remaining area without capturing its own controls on X11.
        var widthLimit = Math.Max(180, ScanViewportContainer.Bounds.Width - 32);
        var heightLimit = Math.Max(180, ScanViewportContainer.Bounds.Height - 28);
        var maximumSize = Math.Min(widthLimit, heightLimit);
        var size = Math.Clamp(maximumSize * _viewportScale, 180, maximumSize);

        ScanArea.Width = size;
        ScanArea.Height = size;
        ViewportScaleLabel.Text = $"{(int)Math.Round(_viewportScale * 100)}%";
    }

    private void SetViewportScale(double scale)
    {
        var nextScale = Math.Clamp(scale, MinViewportScale, MaxViewportScale);
        if (Math.Abs(nextScale - _viewportScale) < 0.001)
            return;

        _viewportScale = nextScale;
        UpdateScanViewportSize();

        if (_closingWithResult || !IsVisible || _screenCapture is null)
            return;

        ResetForRescan($"取景框已调整为 {(int)Math.Round(_viewportScale * 100)}%，正在重新扫描…");
        StartScanningAfterDelay();
    }

    private PixelRect GetScanSurfaceScreenRegion()
    {
        var topLeft = ScanArea.PointToScreen(new Point(0, 0));
        var scale = RenderScaling;
        var width = Math.Max(1, (int)Math.Round(ScanArea.Bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(ScanArea.Bounds.Height * scale));
        return new PixelRect(topLeft.X, topLeft.Y, width, height);
    }

    private void UpdateDetections(IReadOnlyList<QrDetection> detections)
    {
        if (detections.Count == 0)
        {
            ResetPendingCandidate();
            StatusLabel.Text = "未发现完整二维码";
            return;
        }

        var signature = CreateCandidateSignature(detections);
        if (!string.Equals(signature, _pendingCandidateSignature, StringComparison.Ordinal))
        {
            _pendingCandidateSignature = signature;
            _pendingCandidateFrames = 1;
        }
        else
        {
            _pendingCandidateFrames++;
        }

        if (_pendingCandidateFrames < RequiredStableFrames)
        {
            StatusLabel.Text = detections.Count == 1
                ? "发现二维码，正在确认完整范围…"
                : $"发现 {detections.Count} 个二维码，正在确认完整范围…";
            return;
        }

        LockCandidates(detections);
    }

    private static string CreateCandidateSignature(IReadOnlyList<QrDetection> detections)
    {
        return string.Join(
            "\u001f",
            detections
                .Select(static detection => detection.Text)
                .OrderBy(static text => text, StringComparer.Ordinal));
    }

    private void LockCandidates(IReadOnlyList<QrDetection> detections)
    {
        _lockedDetections = detections.ToArray();
        _candidatesLocked = true;
        _scanTimer.Stop();
        ScanLineControl.IsVisible = false;
        ResetPendingCandidate();
        RenderCandidates(_lockedDetections);

        if (_lockedDetections.Count > 1)
        {
            ResetAutoCandidate();
            StatusLabel.Text = $"已锁定 {_lockedDetections.Count} 个二维码，请点击对应的 ✓";
            return;
        }

        var candidate = _lockedDetections[0];
        _autoCandidateKey = candidate.TrackingKey;
        _autoConfirmWatch.Restart();
        _autoConfirmTimer.Start();
        UpdateAutoConfirmStatus();
    }

    private void AutoConfirmTimer_OnTick(object? sender, EventArgs e)
    {
        if (_closingWithResult || !_candidatesLocked || _lockedDetections.Count != 1)
        {
            _autoConfirmTimer.Stop();
            return;
        }

        var candidate = _lockedDetections[0];
        if (!string.Equals(_autoCandidateKey, candidate.TrackingKey, StringComparison.Ordinal))
        {
            ResetForRescan("二维码候选已变化，正在重新扫描…");
            StartScanningAfterDelay();
            return;
        }

        var remaining = AutoConfirmDelay - _autoConfirmWatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            Complete(candidate.Text);
            return;
        }

        UpdateAutoConfirmStatus();
    }

    private void UpdateAutoConfirmStatus()
    {
        var remaining = AutoConfirmDelay - _autoConfirmWatch.Elapsed;
        StatusLabel.Text =
            $"已锁定，{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒后自动确认；也可点击 ✓";
    }

    private void ScannerGeometry_OnChanged()
    {
        if (_closingWithResult || !IsVisible || _screenCapture is null)
            return;

        ResetForRescan("窗口位置或大小已变化，正在重新扫描…");
        StartScanningAfterDelay();
    }

    private void StartScanningAfterDelay()
    {
        _scanTimer.Stop();
        _geometryResumeTimer.Stop();
        _geometryResumeTimer.Start();
    }

    private void GeometryResumeTimer_OnTick(object? sender, EventArgs e)
    {
        _geometryResumeTimer.Stop();

        if (!_closingWithResult && IsVisible && _screenCapture is not null && !_candidatesLocked)
            _scanTimer.Start();
    }

    private void ResetForRescan(string status)
    {
        _scanGeneration++;
        _candidatesLocked = false;
        _lockedDetections = Array.Empty<QrDetection>();
        ScanLineControl.IsVisible = true;
        ResetPendingCandidate();
        ResetAutoCandidate();
        ClearCandidateVisuals();
        StatusLabel.Text = status;
    }

    private void RenderCandidates(IReadOnlyList<QrDetection> detections)
    {
        ClearCandidateVisuals();
        var scale = RenderScaling;

        foreach (var detection in detections)
        {
            var left = detection.PixelBounds.X / scale;
            var top = detection.PixelBounds.Y / scale;
            var width = detection.PixelBounds.Width / scale;
            var height = detection.PixelBounds.Height / scale;

            var accentBrush = new SolidColorBrush(Color.FromRgb(7, 193, 96));
            var lockBox = new Border
            {
                Width = width,
                Height = height,
                BorderThickness = new Thickness(2),
                BorderBrush = accentBrush,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(24, 7, 193, 96)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(lockBox, left);
            Canvas.SetTop(lockBox, top);
            LockLayer.Children.Add(lockBox);

            var selectedDetection = detection;
            var confirmButton = new Button
            {
                Width = 42,
                Height = 42,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 18,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                Background = accentBrush,
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(21),
                Content = "✓"
            };
            confirmButton.PointerPressed += (_, args) =>
            {
                if (args.GetCurrentPoint(confirmButton).Properties.IsLeftButtonPressed)
                {
                    args.Handled = true;
                    Complete(selectedDetection.Text);
                }
            };

            Canvas.SetLeft(
                confirmButton,
                Math.Clamp(left + width - 21, 0, Math.Max(0, ScanArea.Bounds.Width - 42)));
            Canvas.SetTop(
                confirmButton,
                Math.Clamp(top - 21, 0, Math.Max(0, ScanArea.Bounds.Height - 42)));
            ConfirmLayer.Children.Add(confirmButton);
        }
    }

    private void ClearCandidateVisuals()
    {
        LockLayer.Children.Clear();
        ConfirmLayer.Children.Clear();
    }

    private void ResetPendingCandidate()
    {
        _pendingCandidateSignature = null;
        _pendingCandidateFrames = 0;
    }

    private void ResetAutoCandidate()
    {
        _autoConfirmTimer.Stop();
        _autoCandidateKey = null;
        _autoConfirmWatch.Reset();
    }

    private void Complete(string text)
    {
        if (_closingWithResult)
            return;

        _closingWithResult = true;
        StopAllTimers();
        Close(text);
    }

    private void StopAllTimers()
    {
        _scanTimer.Stop();
        _animationTimer.Stop();
        _autoConfirmTimer.Stop();
        _geometryResumeTimer.Stop();
    }

    private void AnimationTimer_OnTick(object? sender, EventArgs e)
    {
        var usableHeight = Math.Max(0, ScanArea.Bounds.Height - ScanLineControl.Bounds.Height);
        if (usableHeight <= 0)
            return;

        _scanLineProgress += _scanLineDirection * 0.012;
        if (_scanLineProgress >= 1)
        {
            _scanLineProgress = 1;
            _scanLineDirection = -1;
        }
        else if (_scanLineProgress <= 0)
        {
            _scanLineProgress = 0;
            _scanLineDirection = 1;
        }

        ScanLineControl.RenderTransform = new TranslateTransform(0, usableHeight * _scanLineProgress);
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is not null)
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ResizeGrip_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }


    private void ShrinkViewportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetViewportScale(_viewportScale - ViewportScaleStep);
    }

    private void ExpandViewportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetViewportScale(_viewportScale + ViewportScaleStep);
    }

    private void FitViewportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetViewportScale(MaxViewportScale);
    }

    private void ScanSurface_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        SetViewportScale(
            _viewportScale + (e.Delta.Y > 0 ? ViewportScaleStep : -ViewportScaleStep));
        e.Handled = true;
    }

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == Avalonia.Controls.WindowState.Maximized
            ? Avalonia.Controls.WindowState.Normal
            : Avalonia.Controls.WindowState.Maximized;
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _closingWithResult = true;
        StopAllTimers();
        Close((string?)null);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Clockwork.Converters;
using Clockwork.ViewModels;

namespace Clockwork.Views;

public partial class MainWindow : Window
{
    private readonly DropShadowEffect _previewShadow = new()
    {
        Color = Colors.Black,
        BlurRadius = 2,
        OffsetX = 0,
        OffsetY = 0,
        Opacity = 1
    };
    private readonly SolidColorBrush _previewLabelBrush = new(Color.FromRgb(255, 212, 0));
    private readonly SolidColorBrush _previewCyanBrush = new(Color.FromRgb(0, 229, 255));
    private readonly SolidColorBrush _previewWhiteBrush = new(Colors.White);
    private SolidColorBrush? _previewBackgroundBrush;
    private byte _previewBackgroundAlpha;
    private bool _previewBackgroundEnabled;
    private bool _previewAppearanceCached;

    private readonly IImage _maximizeIconAsset;
    private readonly IImage _restoreIconAsset;

    private bool _draggingOverlay;
    private Point _dragOffset;
    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Pre-load SVG assets for caption controls so maximize/restore changes stay instant.
        _maximizeIconAsset =
            (IImage)SvgAssetValueConverter.Instance.Convert(
                "avares://Clockwork/Assets/Icons/Window Controls Icons/Maximize.svg",
                typeof(IImage),
                null,
                System.Globalization.CultureInfo.InvariantCulture)!;

        _restoreIconAsset =
            (IImage)SvgAssetValueConverter.Instance.Convert(
                "avares://Clockwork/Assets/Icons/Window Controls Icons/Restore.svg",
                typeof(IImage),
                null,
                System.Globalization.CultureInfo.InvariantCulture)!;

        UpdateMaximizeIcon();
        UpdateResizeState();
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += (_, _) =>
        {
            UpdateOverlayPreviewPosition();
            UpdateOverlayPreviewAppearance();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeIcon();
            UpdateResizeState();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;

        UpdateMaximizeIcon();
        UpdateOverlayPreviewAppearance();
        UpdateOverlayPreviewPosition();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
            vm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.OverlayPositionX)
            or nameof(MainWindowViewModel.OverlayPositionY)
            or nameof(MainWindowViewModel.OverlayScale))
        {
            UpdateOverlayPreviewPosition();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.OverlayBackgroundEnabled)
            or nameof(MainWindowViewModel.OverlayBackgroundOpacity))
        {
            UpdateOverlayPreviewAppearance();
        }
    }

    private void UpdateResizeState()
    {
        var canResize = WindowState != WindowState.Maximized;

        // A maximized window has nothing useful to resize, and disabling the custom resize
        // hit-test regions at the same time removes the resize/non-client ambiguity at the
        // very top edge of the custom caption buttons.
        CanResize = canResize;

        if (ResizeTop is not null)
            ResizeTop.IsHitTestVisible = canResize;
        if (ResizeLeft is not null)
            ResizeLeft.IsHitTestVisible = canResize;
        if (ResizeRight is not null)
            ResizeRight.IsHitTestVisible = canResize;
        if (ResizeBottom is not null)
            ResizeBottom.IsHitTestVisible = canResize;
        if (ResizeNW is not null)
            ResizeNW.IsHitTestVisible = canResize;
        if (ResizeSW is not null)
            ResizeSW.IsHitTestVisible = canResize;
        if (ResizeSE is not null)
            ResizeSE.IsHitTestVisible = canResize;
    }

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIconImage is null)
            return;

        MaximizeIconImage.Source = WindowState == WindowState.Maximized
            ? _restoreIconAsset
            : _maximizeIconAsset;
    }

    private void UpdateOverlayPreviewAppearance()
    {
        if (PreviewOverlay is null || PreviewOverlayValue is null || PreviewOverlayLabel is null || ViewModel is not { } vm)
            return;

        var opacity = double.IsFinite(vm.OverlayBackgroundOpacity)
            ? Math.Clamp(vm.OverlayBackgroundOpacity, 0.0, 100.0)
            : 94.0;
        var alpha = (byte)Math.Round(255.0 * opacity / 100.0);

        if (_previewAppearanceCached && _previewBackgroundEnabled == vm.OverlayBackgroundEnabled &&
            (!vm.OverlayBackgroundEnabled || _previewBackgroundAlpha == alpha))
            return;

        if (vm.OverlayBackgroundEnabled)
        {
            _previewBackgroundBrush ??= new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
            if (_previewBackgroundBrush is not null && _previewBackgroundBrush.Color.A != alpha)
                _previewBackgroundBrush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));

            PreviewOverlay.Background = _previewBackgroundBrush;
            PreviewOverlayValue.Foreground = _previewWhiteBrush;
            PreviewOverlayLabel.Foreground = _previewLabelBrush;
            PreviewOverlay.Effect = null;
        }
        else
        {
            PreviewOverlay.Background = Brushes.Transparent;
            PreviewOverlayValue.Foreground = _previewCyanBrush;
            PreviewOverlayLabel.Foreground = _previewLabelBrush;
            PreviewOverlay.Effect = _previewShadow;
        }

        _previewBackgroundEnabled = vm.OverlayBackgroundEnabled;
        _previewBackgroundAlpha = alpha;
        _previewAppearanceCached = true;
    }

    private void UpdateOverlayPreviewPosition()
    {
        if (OverlayPreviewCanvas is null || PreviewOverlay is null || ViewModel is not { } vm)
            return;

        var canvasWidth = OverlayPreviewCanvas.Bounds.Width;
        var canvasHeight = OverlayPreviewCanvas.Bounds.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0)
            return;

        var scale = vm.OverlayScale / 100.0;
        PreviewOverlay.RenderTransform = new Avalonia.Media.ScaleTransform(scale, scale);

        var previewWidth = PreviewOverlay.Width * scale;
        var previewHeight = PreviewOverlay.Height * scale;
        var x = (canvasWidth - previewWidth) * (vm.OverlayPositionX / 100.0);
        var y = (canvasHeight - previewHeight) * (vm.OverlayPositionY / 100.0);

        Canvas.SetLeft(PreviewOverlay, Math.Max(0, x));
        Canvas.SetTop(PreviewOverlay, Math.Max(0, y));
    }

    private void OnOverlayPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Control source || OverlayPreviewCanvas is null)
            return;

        var point = e.GetPosition(OverlayPreviewCanvas);
        var scale = vm.OverlayScale / 100.0;
        var previewWidth = PreviewOverlay.Width * scale;
        var previewHeight = PreviewOverlay.Height * scale;
        var left = Canvas.GetLeft(PreviewOverlay);
        var top = Canvas.GetTop(PreviewOverlay);

        // Dragging the preview keeps the original click offset. Clicking elsewhere moves the
        // overlay so its top-left corner lands at the pointer location.
        if (source == PreviewOverlay || source == PreviewOverlay.Child)
            _dragOffset = new Point(point.X - left, point.Y - top);
        else
            _dragOffset = new Point(0, 0);

        _draggingOverlay = true;
        OverlayPreviewCanvas.PointerCaptureLost += OnOverlayPreviewPointerCaptureLost;
        e.Pointer.Capture(OverlayPreviewCanvas);
        UpdateOverlayFromPreviewPoint(point, previewWidth, previewHeight);
        e.Handled = true;
    }

    private void OnOverlayPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingOverlay || ViewModel is not { } vm || OverlayPreviewCanvas is null)
            return;

        var point = e.GetPosition(OverlayPreviewCanvas);
        var scale = vm.OverlayScale / 100.0;
        UpdateOverlayFromPreviewPoint(point, PreviewOverlay.Width * scale, PreviewOverlay.Height * scale);
        e.Handled = true;
    }

    private void OnOverlayPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopOverlayDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnOverlayPreviewPointerCaptureLost(object? sender, EventArgs e)
    {
        _draggingOverlay = false;
        if (OverlayPreviewCanvas is not null)
            OverlayPreviewCanvas.PointerCaptureLost -= OnOverlayPreviewPointerCaptureLost;
    }

    private void StopOverlayDrag(IPointer pointer)
    {
        _draggingOverlay = false;
        pointer.Capture(null);
        if (OverlayPreviewCanvas is not null)
            OverlayPreviewCanvas.PointerCaptureLost -= OnOverlayPreviewPointerCaptureLost;
    }

    private void UpdateOverlayFromPreviewPoint(Point point, double previewWidth, double previewHeight)
    {
        if (ViewModel is not { } vm || OverlayPreviewCanvas is null)
            return;

        var maxX = Math.Max(0, OverlayPreviewCanvas.Bounds.Width - previewWidth);
        var maxY = Math.Max(0, OverlayPreviewCanvas.Bounds.Height - previewHeight);
        var x = Math.Clamp(point.X - _dragOffset.X, 0, maxX);
        var y = Math.Clamp(point.Y - _dragOffset.Y, 0, maxY);

        vm.OverlayPositionX = maxX <= 0 ? 0 : x / maxX * 100.0;
        vm.OverlayPositionY = maxY <= 0 ? 0 : y / maxY * 100.0;
    }

    private void OnResetOverlayPositionClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;

        vm.OverlayPositionX = 0.0;
        vm.OverlayPositionY = 0.0;
    }

    private void OnResetOverlaySizeClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.OverlayScale = 100.0;
    }

    private void OnResetOverlayOpacityClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.OverlayBackgroundOpacity = 94.0;
    }

    private void OnResetStatisticsClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ResetStatisticsToDefault();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        // Closing the window is treated as "minimize to tray".
        // Application/OS shutdown is allowed to close the window for real.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;

        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;

        if (!IsVisible)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) =>
        Close();
}

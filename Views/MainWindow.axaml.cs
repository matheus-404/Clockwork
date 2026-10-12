using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Clockwork.Converters;
using Clockwork.Overlay;
using Clockwork.ViewModels;

namespace Clockwork.Views;

public partial class MainWindow : Window
{
    private const int OverlayHotkeyId = 9001;
    private const int GWLP_WNDPROC = -4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

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

    private nint _hwnd;
    private nint _oldWndProc;
    private WndProcDelegate? _wndProcDelegate;
    private bool _isHotkeyRegistered;
    private bool _isRecordingHotkey;

    public MainWindow()
    {
        InitializeComponent();

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
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        SetupHotkeyHook();
        UpdateMaximizeIcon();
        UpdateOverlayPreviewAppearance();
        UpdateOverlayPreviewPosition();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        TeardownHotkeyHook();

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

        if (e.PropertyName is nameof(MainWindowViewModel.ToggleHotkey))
        {
            if (!_isRecordingHotkey && HotkeyRecorderButton is not null)
                HotkeyRecorderButton.Content = ViewModel?.ToggleHotkey ?? MainWindowViewModel.DefaultToggleHotkey;

            UpdateHotkeyRegistration();
        }
    }

    private void SetupHotkeyHook()
    {
        _hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (_hwnd == 0)
            return;

        if (_oldWndProc == 0)
        {
            _wndProcDelegate = HookWndProc;
            _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }

        UpdateHotkeyRegistration();
    }

    private void TeardownHotkeyHook()
    {
        if (_hwnd != 0 && _isHotkeyRegistered)
        {
            Win32.UnregisterHotKey(_hwnd, OverlayHotkeyId);
            _isHotkeyRegistered = false;
        }

        if (_hwnd != 0 && _oldWndProc != 0)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = 0;
            _wndProcDelegate = null;
        }

        _hwnd = 0;
    }

    private void UpdateHotkeyRegistration()
    {
        if (_hwnd == 0)
            return;

        if (_isHotkeyRegistered)
        {
            Win32.UnregisterHotKey(_hwnd, OverlayHotkeyId);
            _isHotkeyRegistered = false;
        }

        var hotkeyStr = ViewModel?.ToggleHotkey ?? MainWindowViewModel.DefaultToggleHotkey;
        if (TryParseHotkey(hotkeyStr, out var modifiers, out var vk))
        {
            _isHotkeyRegistered = Win32.RegisterHotKey(_hwnd, OverlayHotkeyId, modifiers, vk);
        }

        if (HotkeyRecorderButton is not null)
        {
            ToolTip.SetTip(
                HotkeyRecorderButton,
                _isHotkeyRegistered
                    ? "Click to assign a new hotkey (Esc to cancel)"
                    : "Failed to register hotkey with Windows (may conflict with another application)");
        }
    }

    private nint HookWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == Win32.WM_HOTKEY && (int)wParam == OverlayHotkeyId)
        {
            TriggerOverlayToggle();
            return 0;
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    private static void TriggerOverlayToggle()
    {
        if (Application.Current is App app)
        {
            var field = typeof(App).GetField("_overlay", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(app) is OverlayController overlay)
            {
                overlay.ToggleVisibility();
            }
        }
    }

    private void OnHotkeyRecorderClick(object? sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        if (HotkeyRecorderButton is not null)
        {
            HotkeyRecorderButton.Content = "Press shortcut...";
            HotkeyRecorderButton.Focus();
        }
    }

    private void OnHotkeyRecorderKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isRecordingHotkey)
            return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _isRecordingHotkey = false;
            if (HotkeyRecorderButton is not null)
                HotkeyRecorderButton.Content = ViewModel?.ToggleHotkey ?? MainWindowViewModel.DefaultToggleHotkey;
            return;
        }

        if (IsModifierKey(e.Key))
            return;

        var formatted = FormatShortcut(e.KeyModifiers, e.Key);
        if (formatted is not null && TryParseHotkey(formatted, out _, out _))
        {
            _isRecordingHotkey = false;
            if (ViewModel is not null)
                ViewModel.ToggleHotkey = formatted;

            if (HotkeyRecorderButton is not null)
                HotkeyRecorderButton.Content = formatted;
        }
    }

    private void OnHotkeyRecorderLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            _isRecordingHotkey = false;
            if (HotkeyRecorderButton is not null)
                HotkeyRecorderButton.Content = ViewModel?.ToggleHotkey ?? MainWindowViewModel.DefaultToggleHotkey;
        }
    }

    private void OnClearHotkeyClick(object? sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = false;
        if (ViewModel is not null)
            ViewModel.ToggleHotkey = MainWindowViewModel.DefaultToggleHotkey;

        if (HotkeyRecorderButton is not null)
            HotkeyRecorderButton.Content = MainWindowViewModel.DefaultToggleHotkey;
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or
               Key.LeftAlt or Key.RightAlt or
               Key.LeftShift or Key.RightShift or
               Key.LWin or Key.RWin;

    private static string? FormatShortcut(KeyModifiers modifiers, Key key)
    {
        if (IsModifierKey(key))
            return null;

        var parts = new List<string>(5);

        if (modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("Win");
        if (modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");

        string keyStr = key switch
        {
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            Key.OemTilde => "`",
            Key.Tab => "Tab",
            Key.Space => "Space",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Pause => "Pause",
            Key.Scroll => "ScrollLock",
            _ => key.ToString()
        };

        parts.Add(keyStr);
        return string.Join(" + ", parts);
    }

    private static bool TryParseHotkey(string? text, out uint modifiers, out uint vk)
    {
        modifiers = Win32.MOD_NOREPEAT;
        vk = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? keyPart = null;

        foreach (var part in parts)
        {
            if (string.Equals(part, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_CONTROL;
            }
            else if (string.Equals(part, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_ALT;
            }
            else if (string.Equals(part, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_SHIFT;
            }
            else if (string.Equals(part, "Win", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(part, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= Win32.MOD_WIN;
            }
            else
            {
                if (keyPart is not null)
                    return false;

                keyPart = part;
            }
        }

        if (string.IsNullOrEmpty(keyPart))
            return false;

        if (keyPart.StartsWith("F", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(keyPart[1..], out var fNum) && fNum is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (fNum - 1)); // VK_F1 = 0x70
            return true;
        }

        if (keyPart.Length == 1 && char.IsLetter(keyPart[0]))
        {
            vk = char.ToUpperInvariant(keyPart[0]);
            return true;
        }

        if (keyPart.Length == 1 && char.IsAsciiDigit(keyPart[0]))
        {
            vk = keyPart[0];
            return true;
        }

        vk = keyPart.ToUpperInvariant() switch
        {
            "GRAVE" or "`" or "~" => 0xC0,
            "TAB" => 0x09,
            "SPACE" or "SPACEBAR" => 0x20,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "PAUSE" or "BREAK" => 0x13,
            "SCROLLLOCK" or "SCROLL" => 0x91,
            _ => 0
        };

        return vk != 0;
    }

    private void UpdateResizeState()
    {
        var canResize = WindowState != WindowState.Maximized;

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
        if (ResizeNE is not null)
            ResizeNE.IsHitTestVisible = canResize;
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
        if (PreviewOverlay.RenderTransform is Avalonia.Media.ScaleTransform previewScale)
        {
            previewScale.ScaleX = scale;
            previewScale.ScaleY = scale;
        }
        else
        {
            PreviewOverlay.RenderTransform = new Avalonia.Media.ScaleTransform(scale, scale);
        }

        var previewWidth = PreviewOverlay.Width * scale;
        var previewHeight = PreviewOverlay.Height * scale;
        var x = (canvasWidth - previewWidth) * (vm.OverlayPositionX / 100.0);
        var y = (canvasHeight - previewHeight) * (vm.OverlayPositionY / 100.0);

        Canvas.SetLeft(PreviewOverlay, Math.Max(0, x));
        Canvas.SetTop(PreviewOverlay, Math.Max(0, y));
    }

    private void OnOverlayPreviewCanvasSizeChanged(object? sender, SizeChangedEventArgs e) =>
        UpdateOverlayPreviewPosition();

    private void OnOverlayPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is not Visual sourceVisual || OverlayPreviewCanvas is null || PreviewOverlay is null)
            return;

        UpdateOverlayPreviewPosition();
        if (OverlayPreviewCanvas.Bounds.Width <= 0 || OverlayPreviewCanvas.Bounds.Height <= 0)
            return;

        var point = e.GetPosition(OverlayPreviewCanvas);
        var scale = vm.OverlayScale / 100.0;
        var previewWidth = PreviewOverlay.Width * scale;
        var previewHeight = PreviewOverlay.Height * scale;
        var left = Canvas.GetLeft(PreviewOverlay);
        var top = Canvas.GetTop(PreviewOverlay);
        if (!double.IsFinite(left)) left = 0;
        if (!double.IsFinite(top)) top = 0;

        if (sourceVisual == PreviewOverlay || PreviewOverlay.IsVisualAncestorOf(sourceVisual))
            _dragOffset = new Point(point.X - left, point.Y - top);
        else
            _dragOffset = new Point(previewWidth / 2.0, previewHeight / 2.0);

        _draggingOverlay = true;
        OverlayPreviewCanvas.PointerCaptureLost += OnOverlayPreviewPointerCaptureLost;
        e.Pointer.Capture(OverlayPreviewCanvas);
        UpdateOverlayFromPreviewPoint(point, previewWidth, previewHeight);
        e.Handled = true;
    }

    private void OnOverlayPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingOverlay || ViewModel is not { } vm || OverlayPreviewCanvas is null || PreviewOverlay is null)
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
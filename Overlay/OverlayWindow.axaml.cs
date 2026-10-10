using Avalonia.Controls;
using Avalonia.Media;

namespace Clockwork.Overlay;

public partial class OverlayWindow : Window
{
    private double _lastScale = double.NaN;
    private bool _lastBackgroundEnabled;
    private byte _lastBackgroundAlpha;
    private bool _hasAppearance;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    public void SetScale(double scale)
    {
        var clamped = double.IsFinite(scale) ? Math.Clamp(scale, 0.5, 2.0) : 1.0;
        if (Math.Abs(_lastScale - clamped) < 0.000001)
            return;

        if (ScaleContainer.LayoutTransform is ScaleTransform transform)
        {
            transform.ScaleX = clamped;
            transform.ScaleY = clamped;
        }
        else
        {
            ScaleContainer.LayoutTransform = new ScaleTransform(clamped, clamped);
        }

        _lastScale = clamped;
    }

    public void SetAppearance(bool backgroundEnabled, double backgroundOpacity)
    {
        var clampedOpacity = double.IsFinite(backgroundOpacity)
            ? Math.Clamp(backgroundOpacity, 0.0, 100.0)
            : 94.0;
        var alpha = (byte)Math.Round(255.0 * clampedOpacity / 100.0);

        if (_hasAppearance && _lastBackgroundEnabled == backgroundEnabled &&
            (!backgroundEnabled || _lastBackgroundAlpha == alpha))
            return;

        if (backgroundEnabled)
            OverlayBackgroundBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        else
            OverlayBackgroundBorder.Background = Brushes.Transparent;

        OverlayBackgroundBorder.Classes.Set("transparent", !backgroundEnabled);
        _lastBackgroundEnabled = backgroundEnabled;
        _lastBackgroundAlpha = alpha;
        _hasAppearance = true;
    }

    public void EnsureTopmost()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        Win32.BringToTopmost(hwnd);
        ApplyExtendedStyles();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyExtendedStyles();
    }

    private void ApplyExtendedStyles()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
        if (hwnd == 0) return;

        var style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        var targetStyle = style
            | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED
            | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;

        if (style != targetStyle)
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, targetStyle);
    }
}
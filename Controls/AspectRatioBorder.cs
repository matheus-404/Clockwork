using Avalonia;
using Avalonia.Controls;

namespace Clockwork.Controls;

/// <summary>
/// A Border whose desired and arranged size preserves a fixed aspect ratio.
/// It uses the available width when possible and derives the height from it.
/// </summary>
public class AspectRatioBorder : Border
{
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<AspectRatioBorder, double>(nameof(AspectRatio), 16.0 / 9.0);

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ratio = GetValidAspectRatio();
        var widthIsFinite = double.IsFinite(availableSize.Width);
        var heightIsFinite = double.IsFinite(availableSize.Height);

        double width;
        double height;

        if (widthIsFinite && availableSize.Width > 0)
        {
            width = availableSize.Width;
            height = width / ratio;

            if (heightIsFinite && availableSize.Height > 0 && height > availableSize.Height)
            {
                height = availableSize.Height;
                width = height * ratio;
            }
        }
        else if (heightIsFinite && availableSize.Height > 0)
        {
            height = availableSize.Height;
            width = height * ratio;
        }
        else
        {
            // This is only a fallback for a completely unconstrained parent.
            // A Canvas reports a zero desired size, so provide a useful finite
            // default rather than allowing the preview to collapse.
            width = 320.0;
            height = width / ratio;
        }

        // Measure the content using the actual rectangle that will be arranged.
        base.MeasureOverride(new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var ratio = GetValidAspectRatio();
        var width = Math.Max(0.0, finalSize.Width);
        var height = width / ratio;

        if (double.IsFinite(finalSize.Height) && finalSize.Height > 0 && height > finalSize.Height)
        {
            height = finalSize.Height;
            width = height * ratio;
        }

        return base.ArrangeOverride(new Size(width, height));
    }

    private double GetValidAspectRatio() =>
        double.IsFinite(AspectRatio) && AspectRatio > 0.0 ? AspectRatio : 16.0 / 9.0;
}

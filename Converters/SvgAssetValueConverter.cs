using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Platform;

namespace Clockwork.Converters;

/// <summary>
/// Lightweight SVG asset converter for Clockwork's simple icon set. It converts the small,
/// static SVGs to Avalonia DrawingImages once and caches the result, avoiding a retained Skia
/// SVG control dependency for every icon.
/// </summary>
public sealed class SvgAssetValueConverter : IValueConverter
{
    public static readonly SvgAssetValueConverter Instance = new();
    private static readonly ConcurrentDictionary<string, IImage> s_cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string rawUri || string.IsNullOrWhiteSpace(rawUri))
            return null;

        if (s_cache.TryGetValue(rawUri, out var cached))
            return cached;

        try
        {
            var uri = new Uri(rawUri);
            using var stream = AssetLoader.Open(uri);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null)
                return null;

            var viewBox = ParseViewBox(root.Attribute("viewBox")?.Value);
            if (viewBox.width <= 0 || viewBox.height <= 0)
                return null;

            var group = new DrawingGroup();
            // Give the drawing its declared viewBox extents so very small stroke-only icons
            // still retain their full logical canvas instead of being cropped to path bounds.
            group.Children.Add(new GeometryDrawing
            {
                Brush = Brushes.Transparent,
                Geometry = new RectangleGeometry(new Rect(viewBox.x, viewBox.y, viewBox.width, viewBox.height))
            });

            var classStyles = ParseClassStyles(root);
            var inherited = ResolveStyle(root, classStyles, new SvgStyle(null, null, 1.0));
            foreach (var child in root.Elements())
                AddElement(child, group, inherited, classStyles);

            var image = new DrawingImage(group);
            return s_cache.GetOrAdd(rawUri, image);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static void AddElement(
        XElement element,
        DrawingGroup parent,
        SvgStyle inherited,
        IReadOnlyDictionary<string, SvgStyle> classStyles)
    {
        var style = ResolveStyle(element, classStyles, inherited);
        var localName = element.Name.LocalName;

        switch (localName)
        {
            case "g":
                foreach (var child in element.Elements())
                    AddElement(child, parent, style, classStyles);
                break;

            case "path":
                AddPath(element, parent, style);
                break;

            case "circle":
                if (TryDouble(element, "cx", out var cx) &&
                    TryDouble(element, "cy", out var cy) &&
                    TryDouble(element, "r", out var radius))
                {
                    AddDrawing(parent, new EllipseGeometry(new Rect(cx - radius, cy - radius, radius * 2, radius * 2)), style);
                }
                break;

            case "ellipse":
                if (TryDouble(element, "cx", out var ecx) &&
                    TryDouble(element, "cy", out var ecy) &&
                    TryDouble(element, "rx", out var rx) &&
                    TryDouble(element, "ry", out var ry))
                {
                    AddDrawing(parent, new EllipseGeometry(new Rect(ecx - rx, ecy - ry, rx * 2, ry * 2)), style);
                }
                break;

            case "rect":
                if (TryDouble(element, "x", out var x) && TryDouble(element, "y", out var y) &&
                    TryDouble(element, "width", out var width) && TryDouble(element, "height", out var height))
                {
                    AddDrawing(parent, new RectangleGeometry(new Rect(x, y, width, height)), style);
                }
                break;

            case "line":
                if (TryDouble(element, "x1", out var x1) && TryDouble(element, "y1", out var y1) &&
                    TryDouble(element, "x2", out var x2) && TryDouble(element, "y2", out var y2))
                {
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(new Point(x1, y1), false);
                        ctx.LineTo(new Point(x2, y2));
                    }
                    AddDrawing(parent, geometry, new SvgStyle(null, style.Stroke, style.StrokeWidth));
                }
                break;

            case "polyline":
            case "polygon":
                var points = ParsePoints(element.Attribute("points")?.Value);
                if (points.Count < 2)
                    break;

                var polygon = new StreamGeometry();
                using (var ctx = polygon.Open())
                {
                    ctx.BeginFigure(points[0], localName == "polygon");
                    for (var i = 1; i < points.Count; i++)
                        ctx.LineTo(points[i]);
                    if (localName == "polygon")
                        ctx.EndFigure(true);
                }
                AddDrawing(parent, polygon, style);
                break;
        }
    }

    private static void AddPath(XElement element, DrawingGroup parent, SvgStyle style)
    {
        var data = element.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(data))
            return;

        try
        {
            var geometry = StreamGeometry.Parse(data);
            AddDrawing(parent, geometry, style);
        }
        catch
        {
            // One malformed icon should not prevent the rest of the UI from loading.
        }
    }

    private static void AddDrawing(DrawingGroup parent, Geometry geometry, SvgStyle style)
    {
        parent.Children.Add(new GeometryDrawing
        {
            Brush = CreateBrush(style.Fill),
            Pen = CreatePen(style.Stroke, style.StrokeWidth ?? 0),
            Geometry = geometry
        });
    }

    private static IBrush? CreateBrush(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        try { return new SolidColorBrush(Color.Parse(value)); }
        catch { return null; }
    }

    private static Pen? CreatePen(string? value, double width)
    {
        var brush = CreateBrush(value);
        return brush is null || width <= 0 ? null : new Pen(brush, width);
    }

    private static SvgStyle ResolveStyle(XElement element, IReadOnlyDictionary<string, SvgStyle> classStyles, SvgStyle inherited)
    {
        var result = inherited;
        if (element.Attribute("class") is { } classAttribute)
        {
            foreach (var className in classAttribute.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (classStyles.TryGetValue(className, out var classStyle))
                    result = Merge(result, classStyle);
            }
        }

        result = Merge(result, ParseAttributes(element));
        return result;
    }

    private static SvgStyle ParseAttributes(XElement element)
    {
        var fill = element.Attribute("fill")?.Value;
        var stroke = element.Attribute("stroke")?.Value;
        var strokeWidth = TryDouble(element, "stroke-width", out var width) ? width : (double?)null;

        var styleAttribute = element.Attribute("style")?.Value;
        if (!string.IsNullOrWhiteSpace(styleAttribute))
        {
            foreach (var part in styleAttribute.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var pieces = part.Split(':', 2);
                if (pieces.Length != 2) continue;
                switch (pieces[0].Trim())
                {
                    case "fill": fill = pieces[1].Trim(); break;
                    case "stroke": stroke = pieces[1].Trim(); break;
                    case "stroke-width":
                        if (double.TryParse(pieces[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
                            strokeWidth = sw;
                        break;
                }
            }
        }

        return new SvgStyle(fill, stroke, strokeWidth);
    }

    private static SvgStyle Merge(SvgStyle inherited, SvgStyle local) =>
        new(local.Fill ?? inherited.Fill, local.Stroke ?? inherited.Stroke, local.StrokeWidth ?? inherited.StrokeWidth);

    private static Dictionary<string, SvgStyle> ParseClassStyles(XElement root)
    {
        var result = new Dictionary<string, SvgStyle>(StringComparer.Ordinal);
        foreach (var styleElement in root.Descendants().Where(x => x.Name.LocalName == "style"))
        {
            var css = styleElement.Value;
            foreach (var rule in css.Split('}', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = rule.Split('{', 2);
                if (parts.Length != 2) continue;
                var selectors = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries);
                var style = ParseCssStyle(parts[1]);
                foreach (var selector in selectors)
                {
                    var key = selector.Trim().TrimStart('.');
                    if (key.Length > 0)
                        result[key] = style;
                }
            }
        }
        return result;
    }

    private static SvgStyle ParseCssStyle(string css)
    {
        string? fill = null;
        string? stroke = null;
        double? width = null;
        foreach (var part in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(':', 2);
            if (pieces.Length != 2) continue;
            switch (pieces[0].Trim())
            {
                case "fill": fill = pieces[1].Trim(); break;
                case "stroke": stroke = pieces[1].Trim(); break;
                case "stroke-width":
                    if (double.TryParse(pieces[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
                        width = sw;
                    break;
            }
        }
        return new SvgStyle(fill, stroke, width);
    }

    private static (double x, double y, double width, double height) ParseViewBox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default;

        var parts = value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            return default;

        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
               double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
               double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
               double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            ? (x, y, width, height)
            : default;
    }

    private static bool TryDouble(XElement element, string attribute, out double value) =>
        double.TryParse(element.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static List<Point> ParsePoints(string? value)
    {
        var result = new List<Point>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        var parts = value.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                result.Add(new Point(x, y));
        }
        return result;
    }

    private readonly record struct SvgStyle(string? Fill, string? Stroke, double? StrokeWidth);
}

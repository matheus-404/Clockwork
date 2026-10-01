using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Clockwork.Converters;

namespace Clockwork.Services;

internal static class StartupMessageBox
{
    public static async Task ShowAsync(Window owner, string title, string message)
    {
        var window = new Window
        {
            Title = title,
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Clockwork/Assets/Icons/Clockwork.ico"))),
            Width = 500,
            MinWidth = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#0F1115")),
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = 40,
            FontSize = 14,
        };

        var titleBar = new Border
        {
            Height = 40,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#20252E")),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var titleBarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brushes.Transparent,
        };

        var appTitle = new TextBlock
        {
            Text = "Clockwork",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#EEF1F6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            IsHitTestVisible = false,
        };

        appTitle.SetValue(Grid.ColumnProperty, 0);

        var closeButton = new Button
        {
            Width = 46,
            Height = 40,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#EEF1F6")),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = window,
        };
        closeButton.SetValue(Grid.ColumnProperty, 1);

        var closeIcon = LoadIcon("avares://Clockwork/Assets/Icons/Window Controls Icons/Close.svg");
        closeButton.Content = new Image
        {
            Source = closeIcon,
            Width = 10,
            Height = 10,
            Stretch = Avalonia.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        closeButton.PointerEntered += (_, _) =>
            closeButton.Background = new SolidColorBrush(Color.Parse("#C6473B"));
        closeButton.PointerExited += (_, _) =>
            closeButton.Background = Brushes.Transparent;
        closeButton.Click += (_, _) => window.Close();

        titleBarGrid.Children.Add(appTitle);
        titleBarGrid.Children.Add(closeButton);
        titleBar.Child = titleBarGrid;

        titleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                window.BeginMoveDrag(e);
        };

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#EEF1F6")),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9AA3B2")),
            TextWrapping = TextWrapping.Wrap,
        };

        var button = new Button
        {
            Content = "OK",
            Width = 90,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 20, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#6D8DFF")),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            FontSize = 13,
        };
        button.PointerEntered += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#86A3FF"));
        button.PointerExited += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#6D8DFF"));
        button.PointerPressed += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#5D7BE0"));
        button.PointerReleased += (_, _) =>
            button.Background = new SolidColorBrush(Color.Parse("#86A3FF"));
        button.Click += (_, _) => window.Close();

        var content = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(24, 20, 24, 24),
            Children =
            {
                titleBlock,
                messageBlock,
                button,
            },
        };

        window.Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                titleBar,
                content,
            },
        };

        DockPanel.SetDock(titleBar, Dock.Top);

        await window.ShowDialog(owner);
    }

    private static IImage? LoadIcon(string uri)
    {
        try
        {
            return (IImage?)SvgAssetValueConverter.Instance.Convert(
                uri,
                typeof(IImage),
                null,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}

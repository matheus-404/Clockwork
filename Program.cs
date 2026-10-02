using Avalonia;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.Win32;
using Clockwork.Services;
using Velopack;

namespace Clockwork;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        if (!SingleInstance.TryAcquire(out var singleInstance))
            return; // Another Clockwork is running; it was asked to show its window.

        using (singleInstance)
        {
            singleInstance!.StartListening(() =>
                Dispatcher.UIThread.Post(() =>
                    (Application.Current as App)?.ShowMainWindow()));

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz()
            .LogToTrace();
}
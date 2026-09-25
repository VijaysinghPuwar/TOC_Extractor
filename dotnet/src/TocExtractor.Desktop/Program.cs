using Avalonia;
using TocExtractor.App;

namespace TocExtractor.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything touches Playwright: the browser lives in the app's
        // own folder, downloaded on first launch, not in a shared cache.
        BrowserInstaller.UseAppBrowsers();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Also used by the headless UI tests, so they exercise the same setup.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

using Avalonia;
using Avalonia.Threading;
using TocExtractor.App;
using TocExtractor.Desktop.Services;

namespace TocExtractor.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything touches Playwright: the browser lives in the app's
        // own folder, downloaded on first launch, not in a shared cache.
        BrowserInstaller.UseAppBrowsers();
        BrowserInstaller.UseBundledDriver();

        if (args.Contains("--self-test", StringComparer.Ordinal))
        {
            return SelfTest();
        }

        WatchForCrashes();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Anything no code caught goes to the app's log with its stack trace, so
    /// a crash leaves a record of why. A failure on the window's own thread is
    /// logged and survived, so one broken extraction cannot close the others.
    /// </summary>
    private static void WatchForCrashes()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                AppLog.Error("crash", exception, e.IsTerminating ? "the app stopped" : null);
            }

            AppLog.Close();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("unobserved", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("crash", e.Exception, "on the window's thread; the app carried on");
            e.Handled = true;
        };
    }

    /// <summary>Also used by the headless UI tests, so they exercise the same setup.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Checks a packaged build has everything it needs, without opening a
    /// window. The release workflow runs it on every build it publishes, so a
    /// download that is missing its browser driver never reaches anyone.
    /// </summary>
    private static int SelfTest()
    {
        try
        {
            var (node, cli) = BrowserInstaller.FindDriver(BrowserInstaller.DriverRoot);
            var version = typeof(Program).Assembly.GetName().Version;
            Console.WriteLine($"TOC Extractor {version}");
            Console.WriteLine($"driver: {node}");
            Console.WriteLine($"cli:    {cli}");
            Console.WriteLine($"data:   {AppPaths.DataDirectory}");
            Console.WriteLine("self-test passed");
            return 0;
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine("self-test failed: " + exception.Message);
            return 1;
        }
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using TocExtractor.App;
using TocExtractor.Desktop.Services;
using TocExtractor.Desktop.ViewModels;
using TocExtractor.Desktop.Views;

namespace TocExtractor.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppLog.Open(AppPaths.Logs);
            var window = new MainWindow();
            var viewModel = new MainViewModel(
                sessions: null,
                shell: new DesktopShell(window),
                browser: BrowserSetup.Real,
                store: new SettingsStore(AppPaths.LastSession),
                post: action => Dispatcher.UIThread.Post(action),
                log: AppLog.Root,
                applyTheme: UseTheme,
                sitePaces: new TocExtractor.App.Session.SitePaces(AppPaths.SitePaces));
            UseTheme(viewModel.Theme);
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();
            desktop.Exit += (_, _) => AppLog.Close();
            _ = Autopilot.Plan() is { } plan
                ? Autopilot.RunAsync(plan, viewModel, window, desktop)
                : viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void UseTheme(AppTheme theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            var window = new MainWindow();
            var viewModel = new MainViewModel(
                session: null,
                shell: new DesktopShell(window),
                browser: BrowserSetup.Real,
                store: new SettingsStore(AppPaths.LastSession),
                post: action => Dispatcher.UIThread.Post(action));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();
            _ = viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

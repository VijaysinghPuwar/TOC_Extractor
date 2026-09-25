using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop;

/// <summary>
/// Drives the real window through scan, choose and save, for end-to-end
/// testing of a packaged build against a real site.
/// </summary>
/// <remarks>
/// Off unless TOC_AUTOPILOT is set, as "novel-url|from|to". It presses the
/// same commands a person would, then saves a picture of this window only
/// (never the screen) to TOC_AUTOPILOT_SHOT and quits with 0 if every chapter
/// was saved. It adds no behaviour a person could not trigger by hand.
/// </remarks>
internal static class Autopilot
{
    internal sealed record Run(string Url, int From, int To, string? Shot);

    public static Run? Plan()
    {
        var spec = Environment.GetEnvironmentVariable("TOC_AUTOPILOT");
        var parts = spec?.Split('|');
        return parts is { Length: 3 }
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var from)
            && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var to)
            ? new Run(parts[0], from, to, Environment.GetEnvironmentVariable("TOC_AUTOPILOT_SHOT"))
            : null;
    }

    public static async Task RunAsync(Run run, MainViewModel viewModel, Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var code = 1;
        try
        {
            await viewModel.StartAsync().ConfigureAwait(true);
            viewModel.NovelUrl = run.Url;
            await viewModel.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);
            await Shoot(window, run.Shot, "scanned").ConfigureAwait(true);
            if (viewModel.ScanReady)
            {
                viewModel.From = run.From;
                viewModel.To = run.To;
                await viewModel.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
                await Shoot(window, run.Shot, "saved").ConfigureAwait(true);
                code = viewModel.Status.StartsWith("Done.", StringComparison.Ordinal) ? 0 : 2;
            }

            Console.WriteLine($"autopilot: {viewModel.Status} | {viewModel.Problem}");
            foreach (var line in viewModel.Activity)
            {
                Console.WriteLine("activity: " + line);
            }
        }
        finally
        {
            desktop.Shutdown(code);
        }
    }

    private static async Task Shoot(Window window, string? path, string suffix)
    {
        if (path is null)
        {
            return;
        }

        // Let the last reports reach the window before the picture.
        await Task.Delay(800).ConfigureAwait(true);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var size = new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(window);
            bitmap.Save(Path.ChangeExtension(path, null) + "-" + suffix + ".png", PngBitmapEncoderOptions.Default);
        });
    }
}

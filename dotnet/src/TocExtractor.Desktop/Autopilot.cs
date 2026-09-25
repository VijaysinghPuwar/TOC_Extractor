using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TocExtractor.Desktop.ViewModels;

namespace TocExtractor.Desktop;

/// <summary>
/// Drives the real window through scan, choose and save, for end-to-end
/// testing of a packaged build against real sites.
/// </summary>
/// <remarks>
/// Off unless TOC_AUTOPILOT is set, as "novel-url|from|to", or several of
/// those joined by ";;". Each one is started as its own extraction a few
/// seconds after the one before, while that one is still working, the way a
/// person would start a second book without waiting for the first. It
/// presses the same commands a person would, saves pictures of this window
/// only (never the screen) to TOC_AUTOPILOT_SHOT, and quits with 0 if every
/// chapter of every extraction was saved. TOC_AUTOPILOT_OUT, when set, is the
/// folder to save into. It adds no behaviour a person could not trigger by hand.
/// </remarks>
internal static class Autopilot
{
    internal sealed record Run(string Url, int From, int To);

    internal sealed record Plan_(IReadOnlyList<Run> Runs, string? Shot, string? Output);

    public static Plan_? Plan()
    {
        var spec = Environment.GetEnvironmentVariable("TOC_AUTOPILOT");
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        List<Run> runs = [];
        foreach (var one in spec.Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = one.Split('|');
            if (parts is not { Length: 3 }
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var from)
                || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var to))
            {
                return null;
            }

            runs.Add(new Run(parts[0].Trim(), from, to));
        }

        return runs.Count == 0
            ? null
            : new Plan_(runs, Environment.GetEnvironmentVariable("TOC_AUTOPILOT_SHOT"), Environment.GetEnvironmentVariable("TOC_AUTOPILOT_OUT"));
    }

    public static async Task RunAsync(Plan_ plan, MainViewModel viewModel, Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var code = 1;
        try
        {
            await viewModel.StartAsync().ConfigureAwait(true);
            List<(JobViewModel Job, Task Work)> started = [];
            for (var i = 0; i < plan.Runs.Count; i++)
            {
                var run = plan.Runs[i];
                var job = i == 0 ? viewModel.SelectedJob! : Add(viewModel);
                if (plan.Output is { } output)
                {
                    job.OutputDirectory = output;
                }

                job.NovelUrl = run.Url;
                started.Add((job, RunOneAsync(job, run)));
                Console.WriteLine($"autopilot: started extraction {job.Number} at {DateTime.Now:HH:mm:ss}");

                // The next one starts while this one is still at work.
                if (i < plan.Runs.Count - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            Console.WriteLine($"autopilot: {viewModel.RunningText} at {DateTime.Now:HH:mm:ss}");
            foreach (var (job, _) in started)
            {
                Console.WriteLine($"autopilot: #{job.Number} {job.Caption}: {job.RailStatus}");
            }

            await Shoot(window, plan.Shot, "running").ConfigureAwait(true);

            await Task.WhenAll(started.Select(s => s.Work)).ConfigureAwait(true);
            viewModel.SelectedJob = started[0].Job;
            await Shoot(window, plan.Shot, "saved").ConfigureAwait(true);

            code = started.All(s => s.Job.Status.StartsWith("Done.", StringComparison.Ordinal)) ? 0 : 2;
            foreach (var (job, _) in started)
            {
                Console.WriteLine($"autopilot: #{job.Number} {job.Caption}: {job.Status} | {job.Problem} | log {job.LogPath}");
                foreach (var line in job.Activity)
                {
                    Console.WriteLine($"activity #{job.Number}: {line}");
                }
            }
        }
        finally
        {
            desktop.Shutdown(code);
        }
    }

    private static JobViewModel Add(MainViewModel viewModel)
    {
        viewModel.NewJobCommand.Execute(null);
        return viewModel.SelectedJob!;
    }

    private static async Task RunOneAsync(JobViewModel job, Run run)
    {
        await job.ScanCommand.ExecuteAsync(null).ConfigureAwait(true);
        if (!job.ScanReady)
        {
            return;
        }

        job.From = run.From;
        job.To = run.To;
        await job.SaveCommand.ExecuteAsync(null).ConfigureAwait(true);
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

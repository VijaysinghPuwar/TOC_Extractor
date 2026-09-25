using TocExtractor.App;

namespace TocExtractor.Desktop.Services;

/// <summary>The first-run browser download, as two functions so tests can stand in for it.</summary>
public sealed record BrowserSetup(
    Func<CancellationToken, Task<bool>> IsInstalledAsync,
    Func<IProgress<InstallProgress>, CancellationToken, Task> InstallAsync)
{
    public static BrowserSetup Real { get; } = new(
        BrowserInstaller.IsInstalledAsync,
        (progress, token) => BrowserInstaller.InstallAsync(progress, token));

    /// <summary>Already there. For tests and for runs that bring their own browser.</summary>
    public static BrowserSetup Present { get; } = new(
        _ => Task.FromResult(true),
        (_, _) => Task.CompletedTask);
}

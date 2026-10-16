using TocExtractor.Desktop.Services;

namespace TocExtractor.Desktop.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void Opening_the_app_again_shows_the_copy_already_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "toc-single-" + Guid.NewGuid().ToString("N"));
        using var first = SingleInstance.Claim(folder)!;
        using var shown = new ManualResetEventSlim();
        first.Listen(shown.Set);

        Assert.Throws<AlreadyRunningException>(() => SingleInstance.Claim(folder));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_copy_with_its_own_data_folder_runs_beside_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var mine = SingleInstance.Claim(Path.Combine(Path.GetTempPath(), "toc-a-" + Guid.NewGuid().ToString("N")));
        using var test = SingleInstance.Claim(Path.Combine(Path.GetTempPath(), "toc-b-" + Guid.NewGuid().ToString("N")));

        Assert.NotNull(mine);
        Assert.NotNull(test);
    }
}

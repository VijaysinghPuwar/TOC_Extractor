using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Links;

namespace TocExtractor.Core.Tests.Checkpoints;

/// <summary>
/// On Windows a rename over a file fails while another process has it open,
/// and Defender and the search indexer open each new file for a moment. With
/// many books saving at once, a save landing in that moment used to fail the
/// chapter.
/// </summary>
public sealed class WindowsSaveTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "toc-ckpt-win-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_save_outlasts_a_brief_hold_on_the_file()
    {
        Directory.CreateDirectory(this.directory);
        var selectors = SelectorSet.Create("a.ch", "h1", "article");
        var checkpoint = new Checkpoint
        {
            Path = Checkpoint.PathFor(this.directory),
            TocUrl = "https://e.com/toc",
            Fingerprint = Checkpoint.FingerprintOf("https://e.com/toc", selectors),
            Selectors = new Dictionary<string, string>(StringComparer.Ordinal) { ["link"] = "a.ch" },
            LinkSet = ["https://e.com/a"],
        };
        checkpoint.Save();

        // What a scanner does: open the file, with no sharing, for a moment.
        var held = new FileStream(checkpoint.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(120, TestContext.Current.CancellationToken);
                await held.DisposeAsync();
            },
            TestContext.Current.CancellationToken);

        checkpoint.Save();
        await release;

        Assert.True(File.Exists(checkpoint.Path));
        Assert.Empty(Directory.GetFiles(this.directory, ".state-*.tmp"));
    }
}

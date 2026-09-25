namespace TocExtractor.App.Tests;

public sealed class BrowserInstallerTests
{
    [Fact]
    public void A_progress_bar_line_becomes_a_percentage_with_the_size()
    {
        var step = BrowserInstaller.ParseLine("|■■■■■■■■                                                |  40% of 152.6 MiB");

        Assert.NotNull(step);
        Assert.Equal(40, step.Percent);
        Assert.Equal("Downloading (152.6 MiB)", step.Message);
    }

    [Fact]
    public void A_download_announcement_names_the_browser_without_the_url()
    {
        var step = BrowserInstaller.ParseLine(
            "Downloading Chrome for Testing 151.0.7922.34 (playwright chromium v1234) from https://cdn.playwright.dev/x.zip");

        Assert.NotNull(step);
        Assert.Equal("Downloading Chrome for Testing 151.0.7922.34 (playwright chromium v1234)", step.Message);
        Assert.Equal(0, step.Percent);
    }

    [Fact]
    public void Completion_is_reported_as_unpacking()
    {
        var step = BrowserInstaller.ParseLine("Chrome for Testing 151 (playwright chromium v1234) downloaded to /x/chromium-1234");

        Assert.Equal(new InstallProgress("Unpacking", 100), step);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Removing unused browser at /x/chromium-1000")]
    public void Noise_is_ignored(string line)
    {
        Assert.Null(BrowserInstaller.ParseLine(line));
    }

    [Fact]
    public void A_percentage_over_100_is_clamped()
    {
        Assert.Equal(100, BrowserInstaller.ParseLine("| 250% of 1 MiB")?.Percent);
    }

    [Fact]
    public void Install_locations_are_read_from_a_dry_run()
    {
        string[] output =
        [
            "Chrome for Testing 151.0.7922.34 (playwright chromium v1234)",
            "  Install location:    /Users/me/Library/Application Support/TOC Extractor/browsers/chromium-1234",
            "  Download url:        https://cdn.playwright.dev/x.zip",
            "",
            "FFmpeg (playwright ffmpeg v1011)",
            "  Install location:    /Users/me/Library/Application Support/TOC Extractor/browsers/ffmpeg-1011",
        ];

        var locations = BrowserInstaller.InstallLocations(output);

        Assert.Equal(2, locations.Count);
        Assert.EndsWith("browsers/chromium-1234", locations[0], StringComparison.Ordinal);
    }

    [Fact]
    public void The_driver_is_found_beside_the_build()
    {
        var (node, cli) = BrowserInstaller.FindDriver(AppContext.BaseDirectory);

        Assert.True(File.Exists(node), node);
        Assert.True(File.Exists(cli), cli);
    }

    [Fact]
    public void A_missing_driver_is_explained()
    {
        var problem = Assert.Throws<FileNotFoundException>(() => BrowserInstaller.FindDriver(Scratch.Directory()));

        Assert.Contains("download it again", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_browser_folder_is_not_installed()
    {
        // A real dry run against the bundled driver, pointed at an empty folder.
        var original = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Scratch.Directory());
        try
        {
            Assert.False(await BrowserInstaller.IsInstalledAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", original);
        }
    }
}

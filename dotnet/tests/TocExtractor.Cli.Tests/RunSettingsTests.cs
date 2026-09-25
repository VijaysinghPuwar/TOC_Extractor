using TocExtractor.Core.Politeness;

namespace TocExtractor.Cli.Tests;

public sealed class RunSettingsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "toc-cli-" + Guid.NewGuid().ToString("N"));

    private static readonly string[] Selectors =
        ["--link", "a.ch", "--title", "h1", "--content", "article"];

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private static RunSettings Parse(params string[] extra)
    {
        string[] args = ["--toc", "https://e.com/toc", .. Selectors, .. extra];
        return RunSettings.From(CommandLine.Build().Parse(args), args);
    }

    private string WriteProfile(string body)
    {
        Directory.CreateDirectory(this.directory);
        var path = Path.Combine(this.directory, "profile.toml");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void Defaults_match_the_documented_values()
    {
        var settings = Parse();

        Assert.Equal(3, settings.Fetch.Concurrency);
        Assert.Equal(2, settings.Fetch.Retries);
        Assert.Equal(20, settings.Fetch.MaxLinks);
        Assert.Equal("downloads", settings.OutputDirectory);
        Assert.Equal(TimeSpan.FromSeconds(25), settings.Fetch.PageBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(500), settings.Fetch.WaitAfterLoad);
        Assert.True(settings.Fetch.StripAds);
    }

    [Fact]
    public void Milliseconds_on_the_command_line_become_time_spans()
    {
        var settings = Parse("--timeout", "4000", "--wait-after-load", "250");

        Assert.Equal(TimeSpan.FromSeconds(4), settings.Fetch.PageBudget);
        Assert.Equal(TimeSpan.FromMilliseconds(250), settings.Fetch.WaitAfterLoad);
    }

    [Fact]
    public void Max_delay_can_never_be_below_min_delay()
    {
        var settings = Parse("--min-delay", "5", "--max-delay", "1");

        Assert.Equal(TimeSpan.FromSeconds(5), settings.Fetch.MaxDelay);
    }

    // -- profile merging -----------------------------------------------------

    [Fact]
    public void Profile_supplies_what_the_command_line_omitted()
    {
        var path = this.WriteProfile("[selectors]\nlink = \"ol.toc a\"\n[options]\nmax = 7\n");

        string[] args = ["--toc", "https://e.com/toc", "--title", "h1", "--content", "article", "--profile", path];
        var settings = RunSettings.From(CommandLine.Build().Parse(args), args);

        Assert.Equal("ol.toc a", settings.Selectors.Link);
        Assert.Equal(7, settings.Fetch.MaxLinks);
        Assert.Contains("link", settings.AppliedFromProfile);
        Assert.Contains("max", settings.AppliedFromProfile);
    }

    /// <summary>
    /// Comparing a parsed value against its default cannot tell "not given"
    /// from "given, and happens to equal the default", so the profile would
    /// override a flag the user did pass.
    /// </summary>
    [Fact]
    public void An_explicit_flag_beats_the_profile_even_at_its_default_value()
    {
        var path = this.WriteProfile("[options]\nmax = 7\nconcurrency = 9\n");

        string[] args = ["--toc", "https://e.com/toc", .. Selectors, "--profile", path, "--max", "20"];
        var settings = RunSettings.From(CommandLine.Build().Parse(args), args);

        Assert.Equal(20, settings.Fetch.MaxLinks);
        Assert.DoesNotContain("max", settings.AppliedFromProfile);
        Assert.Equal(9, settings.Fetch.Concurrency);
    }

    [Fact]
    public void An_explicit_flag_written_with_an_equals_sign_still_wins()
    {
        var path = this.WriteProfile("[options]\nmax = 7\n");

        string[] args = ["--toc", "https://e.com/toc", .. Selectors, "--profile", path, "--max=20"];
        var settings = RunSettings.From(CommandLine.Build().Parse(args), args);

        Assert.Equal(20, settings.Fetch.MaxLinks);
    }

    // -- the defects ---------------------------------------------------------

    /// <summary>
    /// Bug: the screenshot is taken while collecting, before the dry-run check,
    /// so the two flags together leave a toc.png behind from a run that reports
    /// having written nothing.
    /// </summary>
    [Fact]
    public void A_dry_run_asks_for_no_screenshot_and_no_html()
    {
        var settings = Parse("--dry-run", "--screenshot", "--dump-html");

        Assert.Null(settings.Fetch.ScreenshotPath);
        Assert.False(settings.Fetch.CaptureHtml);
    }

    [Fact]
    public void A_real_run_asks_for_the_screenshot_inside_the_output_folder()
    {
        var settings = Parse("--screenshot", "--out", "/tmp/book");

        Assert.Equal(Path.Combine("/tmp/book", "toc.png"), settings.Fetch.ScreenshotPath);
    }

    /// <summary>
    /// Bug: the browser identifies as whatever --ua set while robots.txt is
    /// always evaluated for the tool's own default, so a custom agent gets
    /// decisions meant for a different one.
    /// </summary>
    [Fact]
    public void The_user_agent_flag_is_the_agent_robots_is_evaluated_for()
    {
        var settings = Parse("--ua", "ResearchBot/2.0");

        Assert.Equal("ResearchBot/2.0", settings.UserAgent);
    }

    [Fact]
    public void The_default_user_agent_is_the_one_robots_rules_name() =>
        Assert.Equal(Robots.DefaultUserAgent, Parse().UserAgent);

    [Fact]
    public void The_user_agent_can_come_from_a_profile()
    {
        var path = this.WriteProfile("[options]\nua = \"ProfileBot/1.0\"\n");

        string[] args = ["--toc", "https://e.com/toc", .. Selectors, "--profile", path];
        var settings = RunSettings.From(CommandLine.Build().Parse(args), args);

        Assert.Equal("ProfileBot/1.0", settings.UserAgent);
    }

    [Fact]
    public void Formats_are_repeatable()
    {
        var settings = Parse("--format", "text", "--format", "jsonl");

        Assert.Equal(["text", "jsonl"], settings.Formats);
    }

    [Fact]
    public void Missing_selectors_are_named()
    {
        string[] args = ["--toc", "https://e.com/toc", "--link", "a.ch"];
        var settings = RunSettings.From(CommandLine.Build().Parse(args), args);

        Assert.False(settings.Selectors.Complete);
        Assert.Equal(["title", "content"], settings.Selectors.Missing);
    }

    [Fact]
    public void A_missing_toc_is_a_usage_error()
    {
        var parsed = CommandLine.Build().Parse(Selectors);

        Assert.NotEmpty(parsed.Errors);
    }
}

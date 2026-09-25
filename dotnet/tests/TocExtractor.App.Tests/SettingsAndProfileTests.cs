using TocExtractor.App.Profiles;
using TocExtractor.App.Session;

namespace TocExtractor.App.Tests;

public sealed class SettingsAndProfileTests
{
    [Fact]
    public void Complete_settings_have_no_problems()
    {
        Assert.Empty(Book.Settings("/tmp/out").Problems());
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var settings = new SessionSettings
        {
            TocUrl = "not a url",
            OutputDirectory = " ",
            Formats = [],
            MaxChapters = 0,
            Concurrency = 20,
            MinDelaySeconds = 3,
            MaxDelaySeconds = 1,
        };

        var problems = settings.Problems();

        Assert.Contains(problems, p => p.Contains("https://", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("link selector", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("title selector", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("content selector", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("output folder", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("output format", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Max chapters", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("between 1 and 8", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("delay range", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_format_is_named()
    {
        var problems = (Book.Settings("/tmp/out") with { Formats = ["text", "epub"] }).Problems();

        Assert.Contains("Unknown format: epub", problems);
    }

    [Fact]
    public void Fetch_options_carry_the_settings_across()
    {
        var options = (Book.Settings("/tmp/out") with { MaxChapters = 7, Concurrency = 2, Retries = 4 })
            .ToFetchOptions(sessionAuthenticated: true);

        Assert.Equal(7, options.MaxLinks);
        Assert.Equal(2, options.Concurrency);
        Assert.Equal(4, options.Retries);
        Assert.True(options.SessionAuthenticated);
        Assert.False(options.DryRun);
    }

    [Fact]
    public void A_saved_profile_loads_back_to_the_same_settings()
    {
        var path = Path.Combine(Scratch.Directory(), "site.toml");
        var settings = Book.Settings("/tmp/out") with
        {
            LinkSelector = "ol.toc a[href*=\"chapter\"]",
            TitleSelector = "h1.title",
            ContentSelector = "div.reader\\content",
            MaxChapters = 42,
            Concurrency = 2,
            Retries = 5,
            MinDelaySeconds = 1.5,
            MaxDelaySeconds = 3,
            IncludeLinks = true,
            Formats = ["text", "jsonl"],
        };

        ProfileWriter.Save(settings.ToProfile(path), path);
        var loaded = new SessionSettings { OutputDirectory = "/tmp/out" }.With(ProfileLoader.Load(path));

        Assert.Equal(settings.LinkSelector, loaded.LinkSelector);
        Assert.Equal(settings.TitleSelector, loaded.TitleSelector);
        Assert.Equal(settings.ContentSelector, loaded.ContentSelector);
        Assert.Equal(42, loaded.MaxChapters);
        Assert.Equal(2, loaded.Concurrency);
        Assert.Equal(5, loaded.Retries);
        Assert.Equal(1.5, loaded.MinDelaySeconds);
        Assert.Equal(3, loaded.MaxDelaySeconds);
        Assert.True(loaded.IncludeLinks);
        Assert.Equal(["text", "jsonl"], loaded.Formats);
    }

    [Fact]
    public void A_profile_can_hold_any_character()
    {
        var path = Path.Combine(Scratch.Directory(), "odd.toml");
        const string Awkward = "a[title=\"x\\y\"]\té中";

        ProfileWriter.Save(new Profile { Path = path, Link = Awkward }, path);

        Assert.Equal(Awkward, ProfileLoader.Load(path).Link);
    }

    [Fact]
    public void The_example_profile_loads_into_the_app()
    {
        var example = Path.Combine(RepoRoot(), "profiles", "example.toml");

        var settings = new SessionSettings().With(ProfileLoader.Load(example));

        Assert.Equal("ol.toc a", settings.LinkSelector);
        Assert.Equal("article.reader", settings.ContentSelector);
        Assert.Equal(25, settings.MaxChapters);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file()
    {
        var directory = Scratch.Directory();
        var path = Path.Combine(directory, "site.toml");

        ProfileWriter.Save(new Profile { Path = path, Link = "a" }, path);

        Assert.Equal(["site.toml"], Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public void The_mac_data_folder_is_application_support()
    {
        var folder = AppPaths.DataDirectoryFor(macOs: true);

        Assert.EndsWith(Path.Combine("Library", "Application Support", AppPaths.AppName), folder, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_the_app_writes_is_relative_to_the_working_directory()
    {
        foreach (var path in new[]
                 {
                     AppPaths.DataDirectory, AppPaths.BrowserProfile, AppPaths.Browsers,
                     AppPaths.Profiles, AppPaths.LastSession, AppPaths.DefaultOutputDirectory,
                 })
        {
            Assert.True(Path.IsPathRooted(path), path);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

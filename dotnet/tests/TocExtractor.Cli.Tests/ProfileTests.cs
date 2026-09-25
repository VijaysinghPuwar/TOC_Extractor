
namespace TocExtractor.Cli.Tests;

public sealed class ProfileTests
{
    private const string Valid = """
        [selectors]
        link = "ol.toc a"
        title = "h1.title"
        content = "article.reader"

        [options]
        min_delay = 1.5
        max = 25
        include_links = false
        formats = ["text", "jsonl"]
        ua = "MyBot/1.0"
        """;

    private static Profile Parse(string text) => ProfileLoader.Parse(text, "my-site.toml");

    [Fact]
    public void Reads_every_supported_key()
    {
        var profile = Parse(Valid);

        Assert.Equal("ol.toc a", profile.Link);
        Assert.Equal("h1.title", profile.Title);
        Assert.Equal("article.reader", profile.Content);
        Assert.Equal(1.5, profile.MinDelay);
        Assert.Equal(25, profile.Max);
        Assert.False(profile.IncludeLinks);
        Assert.Equal(["text", "jsonl"], profile.Formats);
        Assert.Equal("MyBot/1.0", profile.UserAgent);
    }

    [Fact]
    public void Absent_values_stay_null_so_they_cannot_mask_a_flag()
    {
        var profile = Parse("[selectors]\nlink = \"a\"\n");

        Assert.Null(profile.Max);
        Assert.Null(profile.Formats);
        Assert.Null(profile.IncludeLinks);
    }

    /// <summary>A typo silently ignored is a profile that does not do what it says.</summary>
    [Fact]
    public void Unknown_option_names_the_valid_keys()
    {
        var thrown = Assert.Throws<ProfileException>(
            () => Parse("[options]\nconcurency = 2\n"));

        Assert.Contains("unknown key(s) in [options]: concurency", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("concurrency", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_selector_is_refused()
    {
        var thrown = Assert.Throws<ProfileException>(() => Parse("[selectors]\nauthor = \"x\"\n"));

        Assert.Contains("unknown key(s) in [selectors]: author", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bug: only the format list is type-checked, so <c>max = "twenty"</c> loads
    /// and then dies with a TypeError from deep inside option translation. A
    /// sentence naming the file, the key and the expected type belongs there.
    /// </summary>
    [Theory]
    [InlineData("max = \"twenty\"", "max", "a whole number")]
    [InlineData("max = 1.5", "max", "a whole number")]
    [InlineData("concurrency = true", "concurrency", "a whole number")]
    [InlineData("min_delay = \"slow\"", "min_delay", "a number")]
    [InlineData("include_links = \"yes\"", "include_links", "true or false")]
    [InlineData("out = 7", "out", "a string")]
    [InlineData("formats = \"text\"", "formats", "a list of strings")]
    [InlineData("formats = [1, 2]", "formats", "a list of strings")]
    [InlineData("ua = 12", "ua", "a string")]
    public void Wrong_type_is_refused_by_name(string line, string key, string expected)
    {
        var thrown = Assert.Throws<ProfileException>(() => Parse($"[options]\n{line}\n"));

        Assert.Contains("my-site.toml", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(key, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_type_says_what_was_found()
    {
        var thrown = Assert.Throws<ProfileException>(() => Parse("[options]\nmax = \"twenty\"\n"));

        Assert.Contains("but is the string \"twenty\"", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_selector_that_is_not_a_string_is_refused() =>
        Assert.Throws<ProfileException>(() => Parse("[selectors]\nlink = 7\n"));

    [Fact]
    public void A_section_that_is_not_a_table_is_refused()
    {
        var thrown = Assert.Throws<ProfileException>(() => Parse("selectors = 7\n"));

        Assert.Contains("[selectors] must be a table", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_toml_names_the_file()
    {
        var thrown = Assert.Throws<ProfileException>(() => Parse("[options\nmax = 1\n"));

        Assert.Contains("my-site.toml is not valid TOML", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_refused_by_path()
    {
        var thrown = Assert.Throws<ProfileException>(
            () => ProfileLoader.Load(Path.Combine(Path.GetTempPath(), "no-such-profile.toml")));

        Assert.Contains("no profile at", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_profile_is_valid_and_supplies_nothing()
    {
        var profile = Parse("");

        Assert.Null(profile.Link);
        Assert.Null(profile.Max);
    }

    /// <summary>The shipped example must load, or it is not an example.</summary>
    [Fact]
    public void The_repository_example_profile_loads()
    {
        var path = Path.Combine(RepositoryRoot(), "profiles", "example.toml");
        Assert.True(File.Exists(path), path);

        var profile = ProfileLoader.Load(path);

        Assert.Equal("ol.toc a", profile.Link);
        Assert.Equal(["text", "jsonl"], profile.Formats);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "profiles")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

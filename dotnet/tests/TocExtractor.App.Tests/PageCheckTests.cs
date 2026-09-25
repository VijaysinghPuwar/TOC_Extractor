using TocExtractor.App.Session;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

public sealed class PageCheckTests
{
    private const string CloudflareCheck =
        "<html><head><title>Just a moment...</title></head><body>"
        + "<script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\"></script></body></html>";

    private const string SignInPage =
        "<html><body><form><input name=\"email\"><input type=\"password\" name=\"pw\"></form></body></html>";

    [Theory]
    [InlineData(CloudflareCheck, Obstacle.HumanCheck)]
    [InlineData("<div class=\"g-recaptcha\"></div>", Obstacle.HumanCheck)]
    [InlineData("<p>Verify you are human by completing the action below.</p>", Obstacle.HumanCheck)]
    [InlineData(SignInPage, Obstacle.SignIn)]
    [InlineData("<input type='password'>", Obstacle.SignIn)]
    [InlineData("<ol class=\"toc\"><li><a href=\"/1\">One</a></li></ol>", Obstacle.None)]
    [InlineData("", Obstacle.None)]
    [InlineData(null, Obstacle.None)]
    public void Recognises_what_is_in_the_way(string? html, Obstacle expected)
    {
        Assert.Equal(expected, PageCheck.Detect(html));
    }

    [Theory]
    [InlineData(Obstacle.HumanCheck, "verify you're human")]
    [InlineData(Obstacle.SignIn, "sign in")]
    [InlineData(Obstacle.None, "matched no chapter links")]
    public void Advice_says_what_to_do(Obstacle obstacle, string expected)
    {
        Assert.Contains(expected, PageCheck.Advice(obstacle), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CloudflareCheck, Obstacle.HumanCheck)]
    [InlineData(SignInPage, Obstacle.SignIn)]
    public async Task An_empty_test_explains_a_check_or_sign_in_page(string html, Obstacle expected)
    {
        var pages = new Dictionary<string, StubPage>(StringComparer.Ordinal)
        {
            [Book.Toc] = new StubPage { Links = [], Html = html },
        };
        var (session, _) = Book.Session(pages, supportsCapture: true);
        await using var _ = session;
        var token = TestContext.Current.CancellationToken;
        await session.LaunchAsync(Book.Settings(Scratch.Directory()), token);
        await session.ConfirmAsync(token);

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()), token);

        Assert.Equal(0, preview.ChapterCount);
        Assert.Equal(expected, preview.Obstacle);
        Assert.Equal(PageCheck.Advice(expected), preview.SampleProblem);
    }

    [Fact]
    public async Task A_page_with_chapters_is_never_called_a_check()
    {
        // Real pages carry challenge scripts and login boxes in their headers.
        var pages = Book.Pages(2);
        pages[Book.Toc] = new StubPage { Links = pages[Book.Toc].Links, Html = CloudflareCheck + SignInPage };
        var (session, _) = Book.Session(pages, supportsCapture: true);
        await using var _ = session;
        var token = TestContext.Current.CancellationToken;
        await session.LaunchAsync(Book.Settings(Scratch.Directory()), token);
        await session.ConfirmAsync(token);

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()), token);

        Assert.Equal(2, preview.ChapterCount);
        Assert.Equal(Obstacle.None, preview.Obstacle);
        Assert.Null(preview.SampleProblem);
    }
}

public sealed class ScanCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_check_that_never_passes_says_so_after_three_tries()
    {
        var waits = 0;
        var scanner = new Scanning.NovelScanner(
            new AlwaysChecking(), new Core.Politeness.UrlGuard(resolver: new PublicResolver()),
            Core.Politeness.RobotsPolicy.Missing("https://e.com"), new Core.Politeness.RateLimiter(TimeSpan.Zero),
            onHumanCheck: (_, _) =>
            {
                waits++;
                return Task.FromResult(true);
            });

        var scan = await scanner.ScanAsync("https://e.com/novel", Token);

        Assert.Equal(3, waits);
        Assert.Equal(Obstacle.HumanCheck, scan.Obstacle);
        Assert.Equal(Scanning.NovelScanner.CheckWontPass, scan.Problem);
    }

    private sealed class AlwaysChecking : Core.Pages.IPageProbe
    {
        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default) =>
            throw new Core.Pages.HumanCheckException();
    }
}

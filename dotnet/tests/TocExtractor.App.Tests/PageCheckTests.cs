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

    [Fact]
    public async Task Once_a_check_has_failed_no_other_page_asks_the_person_again()
    {
        // The novel page reads; every other page is behind a check that
        // does not pass, as on a site whose pages redirect to a broken one.
        var waits = 0;
        var scanner = new Scanning.NovelScanner(
            new OnlyTheNovelPage(), new Core.Politeness.UrlGuard(resolver: new PublicResolver()),
            Core.Politeness.RobotsPolicy.Missing("https://e.com"), new Core.Politeness.RateLimiter(TimeSpan.Zero),
            onHumanCheck: (_, _) =>
            {
                waits++;
                return Task.FromResult(false);
            });

        var scan = await scanner.ScanAsync("https://e.com/novel", Token);

        Assert.Equal(1, waits);
        Assert.Equal(Obstacle.HumanCheck, scan.Obstacle);
        Assert.Equal(Scanning.NovelScanner.CheckWontPass, scan.Problem);
    }

    [Fact]
    public async Task A_novel_page_robots_txt_closes_says_so_rather_than_could_not_be_read()
    {
        // Only the home page and assets are open to tools.
        var robots = Core.Politeness.RobotsPolicy.Parse(
            "User-agent: *\nAllow: /$\nAllow: /css/\nDisallow: /\n", "https://e.com", Core.Politeness.Robots.DefaultUserAgent);
        var scanner = new Scanning.NovelScanner(
            new AlwaysChecking(), new Core.Politeness.UrlGuard(resolver: new PublicResolver()),
            robots, new Core.Politeness.RateLimiter(TimeSpan.Zero));

        var scan = await scanner.ScanAsync("https://e.com/book/a-novel", Token);

        Assert.Equal(Scanning.NovelScanner.RobotsRefused, scan.Problem);
    }

    private sealed class OnlyTheNovelPage : Core.Pages.IPageProbe
    {
        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default)
        {
            if (url != "https://e.com/novel")
            {
                throw new Core.Pages.HumanCheckException();
            }

            var chapters = string.Join(",", Enumerable.Range(1, 5).Select(n =>
                $$"""{"url":"https://e.com/novel/chapter-{{n}}","number":{{n}},"title":"Chapter {{n}}","key":"e.com/novel|chapter-"}"""));
            return Task.FromResult((url, $$"""
                {"title":"Novel","key":"e.com/novel|chapter-","chapters":[{{chapters}}],
                 "pages":[{"url":"https://e.com/novel/2","text":"2","pattern":"https://e.com/novel/#"},
                          {"url":"https://e.com/novel/3","text":"3","pattern":"https://e.com/novel/#"}],
                 "lists":[],"firsts":[]}
                """));
        }
    }

    private sealed class AlwaysChecking : Core.Pages.IPageProbe
    {
        public Task<(string FinalUrl, string Json)> ProbeAsync(string url, string script, TimeSpan settle, CancellationToken cancellationToken = default) =>
            throw new Core.Pages.HumanCheckException();
    }
}

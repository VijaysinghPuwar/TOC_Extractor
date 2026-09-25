using TocExtractor.App.Session;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

public sealed class SessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Launch_opens_the_contents_page_and_waits_for_the_person()
    {
        var (session, source) = Book.Session(Book.Pages(3));
        await using var _ = session;

        await session.LaunchAsync(Book.Settings(Scratch.Directory()), Token);

        Assert.Equal(SessionPhase.SigningIn, session.Phase);
        Assert.Equal([Book.Toc], source.UrlsLoaded);
    }

    [Fact]
    public async Task Launch_does_not_need_the_selectors_yet()
    {
        var (session, _) = Book.Session(Book.Pages(1));
        await using var _ = session;
        var settings = Book.Settings(Scratch.Directory()) with { LinkSelector = "", ContentSelector = "" };

        await session.LaunchAsync(settings, Token);

        Assert.Equal(SessionPhase.SigningIn, session.Phase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.com/toc")]
    [InlineData("ftp://example.com/toc")]
    [InlineData("file:///etc/passwd")]
    public async Task Launch_refuses_an_address_that_is_not_a_web_page(string address)
    {
        var (session, _) = Book.Session(Book.Pages(1));
        await using var _ = session;

        var problem = await Assert.ThrowsAsync<SessionException>(
            () => session.LaunchAsync(Book.Settings(Scratch.Directory()) with { TocUrl = address }, Token));

        Assert.Contains("https://", problem.Message, StringComparison.Ordinal);
        Assert.Equal(SessionPhase.Idle, session.Phase);
    }

    [Fact]
    public async Task Launch_refuses_a_private_address()
    {
        var (session, _) = Book.Session(Book.Pages(1));
        await using var _ = session;

        await Assert.ThrowsAsync<SessionException>(
            () => session.LaunchAsync(Book.Settings(Scratch.Directory()) with { TocUrl = "http://127.0.0.1/toc" }, Token));
        Assert.Equal(SessionPhase.Idle, session.Phase);
    }

    [Fact]
    public async Task A_failed_launch_closes_the_browser_and_returns_to_the_start()
    {
        var (session, source) = Book.Session(Book.Pages(0).Where(p => p.Key != Book.Toc).ToDictionary());
        await using var _ = session;

        var problem = await Assert.ThrowsAsync<SessionException>(
            () => session.LaunchAsync(Book.Settings(Scratch.Directory()), Token));

        Assert.StartsWith("Could not open the contents page", problem.Message, StringComparison.Ordinal);
        Assert.True(source.Closed);
        Assert.Equal(SessionPhase.Idle, session.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirm_reports_whether_the_browser_carries_a_session(bool cookies)
    {
        var (session, _) = Book.Session(Book.Pages(1), authenticated: cookies);
        await using var _ = session;
        await session.LaunchAsync(Book.Settings(Scratch.Directory()), Token);

        var authenticated = await session.ConfirmAsync(Token);

        Assert.Equal(cookies, authenticated);
        Assert.Equal(cookies, session.SessionAuthenticated);
        Assert.Equal(SessionPhase.Ready, session.Phase);
    }

    [Fact]
    public async Task Steps_cannot_be_taken_out_of_order()
    {
        var (session, _) = Book.Session(Book.Pages(1));
        await using var _ = session;
        var settings = Book.Settings(Scratch.Directory());

        await Assert.ThrowsAsync<SessionException>(() => session.ConfirmAsync(Token));
        await Assert.ThrowsAsync<SessionException>(() => session.PreviewAsync(settings, Token));
        await Assert.ThrowsAsync<SessionException>(
            () => session.ExtractAsync(settings, new RecordingObserver(), Token));

        await session.LaunchAsync(settings, Token);
        await Assert.ThrowsAsync<SessionException>(() => session.PreviewAsync(settings, Token));
        await Assert.ThrowsAsync<SessionException>(() => session.LaunchAsync(settings, Token));
    }

    [Fact]
    public async Task Preview_counts_the_chapters_and_reads_the_first_one()
    {
        var (session, _) = await ReadySession(Book.Pages(7));
        await using var _ = session;

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()), Token);

        Assert.Equal(7, preview.ChapterCount);
        Assert.Equal(5, preview.FirstChapters.Count);
        Assert.Equal("https://e.com/ch/1", preview.FirstChapters[0]);
        Assert.Equal("Chapter 1", preview.SampleTitle);
        Assert.Contains("body of chapter 1", preview.SampleExcerpt, StringComparison.Ordinal);
        Assert.Equal(11, preview.SampleWords);
        Assert.Null(preview.SampleProblem);
        Assert.Equal(SessionPhase.Ready, session.Phase);
    }

    [Fact]
    public async Task Preview_writes_nothing()
    {
        var output = Path.Combine(Scratch.Directory(), "not-created");
        var (session, _) = await ReadySession(Book.Pages(2));
        await using var _ = session;

        await session.PreviewAsync(Book.Settings(output), Token);

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task Preview_explains_a_link_selector_that_matched_nothing()
    {
        var (session, _) = await ReadySession(Book.Pages(0));
        await using var _ = session;

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()), Token);

        Assert.Equal(0, preview.ChapterCount);
        Assert.Contains("link selector matched no chapter links", preview.SampleProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_explains_a_content_selector_that_matched_nothing()
    {
        var pages = Book.Pages(2);
        pages["https://e.com/ch/1"] = new StubPage { MissingSelector = true };
        var (session, _) = await ReadySession(pages);
        await using var _ = session;

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()), Token);

        Assert.Equal(2, preview.ChapterCount);
        Assert.Null(preview.SampleTitle);
        Assert.Contains("matched nothing", preview.SampleProblem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_reports_what_robots_txt_said()
    {
        const string Robots = "User-agent: *\nDisallow: /ch/3\nCrawl-delay: 2\n";
        var (session, _) = await ReadySession(Book.Pages(4), robots: Robots);
        await using var _ = session;

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()), Token);

        Assert.True(preview.Robots.Found);
        Assert.Equal(TimeSpan.FromSeconds(2), preview.Robots.CrawlDelay);
        Assert.Equal(1, preview.Robots.RuleCount);
        Assert.True(preview.Robots.ContentsPageAllowed);
        Assert.Equal(3, preview.ChapterCount);
        Assert.Equal(1, preview.Skipped.Values.Sum());
    }

    [Fact]
    public async Task Preview_honours_the_max_chapter_setting()
    {
        var (session, _) = await ReadySession(Book.Pages(9));
        await using var _ = session;

        var preview = await session.PreviewAsync(Book.Settings(Scratch.Directory()) with { MaxChapters = 4 }, Token);

        Assert.Equal(4, preview.ChapterCount);
        Assert.Equal(5, preview.BeyondMax);
    }

    [Fact]
    public async Task Extract_writes_every_chapter_and_reports_each_one()
    {
        var output = Scratch.Directory();
        var (session, _) = await ReadySession(Book.Pages(3));
        await using var _ = session;
        var observer = new RecordingObserver();

        var result = await session.ExtractAsync(
            Book.Settings(output) with { Formats = ["text", "markdown"] }, observer, Token);

        Assert.Equal(Pipeline.PipelineOutcome.Ok, result.Outcome);
        Assert.Equal(3, observer.Records.Count);
        Assert.True(File.Exists(Path.Combine(output, "001 - Chapter 1.txt")));
        Assert.True(File.Exists(Path.Combine(output, "combined.txt")));
        Assert.True(File.Exists(Path.Combine(output, "book.md")));
        Assert.Equal(SessionPhase.Ready, session.Phase);
    }

    [Fact]
    public async Task A_second_run_resumes_and_fetches_only_what_is_new()
    {
        var output = Scratch.Directory();
        var (first, _) = await ReadySession(Book.Pages(2));
        await using (first)
        {
            await first.ExtractAsync(Book.Settings(output), new RecordingObserver(), Token);
        }

        var (second, source) = await ReadySession(Book.Pages(3));
        await using var _ = second;
        var observer = new RecordingObserver();
        await second.ExtractAsync(Book.Settings(output), observer, Token);

        Assert.Equal(["https://e.com/ch/1", "https://e.com/ch/2"], observer.Resumed);
        Assert.Equal(["https://e.com/ch/3"], [.. observer.Records.Select(record => record.RequestedUrl)]);
        Assert.DoesNotContain("https://e.com/ch/1", source.UrlsLoaded);
    }

    [Fact]
    public async Task A_failed_chapter_is_reported_and_the_rest_still_land()
    {
        var output = Scratch.Directory();
        var pages = Book.Pages(3);
        pages["https://e.com/ch/2"] = new StubPage { MissingSelector = true };
        var (session, _) = await ReadySession(pages);
        await using var _ = session;
        var observer = new RecordingObserver();

        var result = await session.ExtractAsync(Book.Settings(output), observer, Token);

        Assert.Equal(Pipeline.PipelineOutcome.Failed, result.Outcome);
        Assert.Equal(2, observer.Records.Count);
        var failure = Assert.Single(observer.Failures);
        Assert.Equal("https://e.com/ch/2", failure.Url);
        Assert.Equal("selector_not_found", failure.Reason);
    }

    [Fact]
    public async Task Changing_the_address_after_launch_is_refused()
    {
        var (session, _) = await ReadySession(Book.Pages(1));
        await using var _ = session;

        var problem = await Assert.ThrowsAsync<SessionException>(
            () => session.PreviewAsync(Book.Settings(Scratch.Directory()) with { TocUrl = "https://e.com/other" }, Token));

        Assert.Contains("changed since the browser opened", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_chapters_at_once_than_the_browser_was_opened_for_is_refused()
    {
        var (session, _) = await ReadySession(Book.Pages(1));
        await using var _ = session;

        await Assert.ThrowsAsync<SessionException>(
            () => session.ExtractAsync(
                Book.Settings(Scratch.Directory()) with { Concurrency = 3 }, new RecordingObserver(), Token));
    }

    [Fact]
    public async Task Close_releases_the_browser_and_allows_a_fresh_launch()
    {
        var (session, source) = await ReadySession(Book.Pages(1));
        await using var _ = session;

        await session.CloseAsync();

        Assert.True(source.Closed);
        Assert.Equal(SessionPhase.Idle, session.Phase);
        Assert.False(session.SessionAuthenticated);
    }

    [Fact]
    public async Task Every_phase_change_is_announced()
    {
        var (session, _) = Book.Session(Book.Pages(1));
        await using var _ = session;
        List<SessionPhase> seen = [];
        session.PhaseChanged += (_, phase) => seen.Add(phase);

        await session.LaunchAsync(Book.Settings(Scratch.Directory()), Token);
        await session.ConfirmAsync(Token);
        await session.PreviewAsync(Book.Settings(Scratch.Directory()), Token);

        Assert.Equal(
            [SessionPhase.Launching, SessionPhase.SigningIn, SessionPhase.Ready, SessionPhase.Previewing, SessionPhase.Ready],
            seen);
    }

    [Fact]
    public void A_long_excerpt_is_cut_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 400));

        var excerpt = ExtractionSession.Excerpt(text);

        Assert.True(excerpt.Length <= 604);
        Assert.EndsWith("word ...", excerpt, StringComparison.Ordinal);
    }

    private static async Task<(ExtractionSession Session, StubPageSource Source)> ReadySession(
        Dictionary<string, StubPage> pages,
        string? robots = null)
    {
        var (session, source) = Book.Session(pages, robots);
        await session.LaunchAsync(Book.Settings(Scratch.Directory()), Token);
        await session.ConfirmAsync(Token);
        return (session, source);
    }
}

using TocExtractor.Core.Pages;

namespace TocExtractor.Core.Tests.Fetching;

/// <summary>One canned page.</summary>
public sealed class StubPage
{
    public IReadOnlyList<object?> Links { get; init; } = [];

    public string Title { get; init; } = "Chapter";

    public string Body { get; init; } = "Body text.";

    public string Html { get; init; } = "<html></html>";

    public string? RedirectTo { get; init; }

    /// <summary>Fail the first N loads, then succeed. How retry behaviour is exercised.</summary>
    public int FailTimes { get; init; }

    public Func<string, PageException> Failure { get; init; } =
        static message => new PageTimeoutException(message);

    public bool MissingSelector { get; init; }

    /// <summary>Real time, for the one test that needs the budget to actually elapse.</summary>
    public TimeSpan Hang { get; init; }

    internal int FailuresServed { get; set; }
}

/// <summary>A dictionary-backed page source. No socket, no browser, no port.</summary>
/// <remarks>
/// Retry rules, concurrency, politeness composition and checkpoint behaviour are
/// all decided by the fetch loop, and none of them need a real page — so none of
/// those tests should pay for one.
/// </remarks>
public sealed class StubPageSource(
    IReadOnlyDictionary<string, StubPage> pages,
    Func<TimeSpan>? clock = null,
    bool supportsCapture = false,
    int maxConcurrent = 1,
    bool authenticated = false) : IPageSource
{
    private readonly Dictionary<string, StubPage> pages =
        pages.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

    private readonly Func<TimeSpan> clock = clock ?? (() => TimeSpan.Zero);
    private readonly Lock gate = new();
    private int inFlight;

    /// <remarks>
    /// A real page cannot serve two navigations at once: concurrent calls abort
    /// each other. Defaulting to unlimited let the fetch loop drive one page from
    /// several workers while every test still passed, so a caller has to state
    /// how many pages it believes it has.
    /// </remarks>
    private readonly int maxConcurrent = Math.Max(1, maxConcurrent);

    public List<(TimeSpan When, string Url)> Loads { get; } = [];

    public int MaxObservedConcurrency { get; private set; }

    public bool Closed { get; private set; }

    public IReadOnlyList<string> UrlsLoaded => [.. this.Loads.Select(entry => entry.Url)];

    public IReadOnlyList<TimeSpan> LoadTimesFor(string url) =>
        [.. this.Loads.Where(entry => entry.Url == url).Select(entry => entry.When)];

    public int AttemptsFor(string url) => this.Loads.Count(entry => entry.Url == url);

    public Task<bool> HasSessionCookiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(authenticated);

    public Task<string> OpenPageAsync(string url, CancellationToken cancellationToken = default)
    {
        this.Enter();
        try
        {
            return Task.FromResult(this.Resolve(url).FinalUrl);
        }
        finally
        {
            this.Leave();
        }
    }

    public Task<TocPage> LoadTocAsync(
        string url,
        string linkSelector,
        bool captureHtml = false,
        string? screenshotPath = null,
        CancellationToken cancellationToken = default)
    {
        if ((captureHtml || screenshotPath is not null) && !supportsCapture)
        {
            throw new CaptureUnsupportedException(
                "the stub page source has no renderer; HTML dumps and screenshots "
                + "need the browser-backed source");
        }

        var (page, finalUrl) = this.Resolve(url);
        return Task.FromResult(new TocPage(
            url, finalUrl, page.Links, captureHtml ? page.Html : null));
    }

    public async Task<ChapterPage> LoadChapterAsync(
        string url,
        string titleSelector,
        string contentSelector,
        CancellationToken cancellationToken = default)
    {
        this.Enter();
        try
        {
            var (page, finalUrl) = this.Resolve(url);

            if (page.Hang > TimeSpan.Zero)
            {
                await Task.Delay(page.Hang, cancellationToken).ConfigureAwait(false);
            }

            return page.MissingSelector
                ? throw new SelectorNotFoundException($"{contentSelector} matched nothing on {finalUrl}")
                : new ChapterPage(url, finalUrl, page.Title, page.Body);
        }
        finally
        {
            this.Leave();
        }
    }

    public ValueTask DisposeAsync()
    {
        this.Closed = true;
        return ValueTask.CompletedTask;
    }

    private void Enter()
    {
        lock (this.gate)
        {
            this.inFlight++;
            this.MaxObservedConcurrency = Math.Max(this.MaxObservedConcurrency, this.inFlight);
            if (this.inFlight > this.maxConcurrent)
            {
                var seen = this.inFlight;
                this.inFlight--;
                throw new PageException(
                    $"{seen} concurrent loads against {this.maxConcurrent} page(s): "
                    + "a real page aborts the earlier navigation");
            }
        }
    }

    private void Leave()
    {
        lock (this.gate)
        {
            this.inFlight--;
        }
    }

    private (StubPage Page, string FinalUrl) Resolve(string url)
    {
        lock (this.gate)
        {
            this.Loads.Add((this.clock(), url));
        }

        List<string> seen = [];
        var current = url;
        while (true)
        {
            if (seen.Contains(current))
            {
                throw new PageException($"redirect loop: {string.Join(" -> ", [.. seen, current])}");
            }

            seen.Add(current);

            if (!this.pages.TryGetValue(current, out var page))
            {
                throw new PageException($"no stub page registered for {current}");
            }

            if (page.RedirectTo is not null)
            {
                current = page.RedirectTo;
                continue;
            }

            lock (this.gate)
            {
                if (page.FailuresServed < page.FailTimes)
                {
                    page.FailuresServed++;
                    throw page.Failure($"stub failure {page.FailuresServed} for {current}");
                }
            }

            return (page, current);
        }
    }
}

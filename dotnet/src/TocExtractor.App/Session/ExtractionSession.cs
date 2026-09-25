using TocExtractor.App.Pipeline;
using TocExtractor.Browser;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Text;

namespace TocExtractor.App.Session;

/// <summary>Where the desktop flow is. The window enables controls from this alone.</summary>
public enum SessionPhase
{
    /// <summary>No browser yet.</summary>
    Idle,

    Launching,

    /// <summary>The browser is on the contents page, waiting for the person to sign in if they need to.</summary>
    SigningIn,

    /// <summary>Confirmed. Selectors can be tested and extraction started.</summary>
    Ready,

    Previewing,

    Extracting,

    Stopping,
}

/// <summary>What the site's robots.txt said, in terms a person can read.</summary>
public sealed record RobotsSummary(
    string RobotsUrl,
    bool Found,
    TimeSpan? CrawlDelay,
    int RuleCount,
    bool ContentsPageAllowed);

/// <summary>What "Test selectors" found, before anything is saved.</summary>
public sealed record SelectorPreview(
    int ChapterCount,
    IReadOnlyList<string> FirstChapters,
    IReadOnlyDictionary<string, int> Skipped,
    int BeyondMax,
    RobotsSummary Robots,
    string? SampleTitle = null,
    string? SampleExcerpt = null,
    int SampleWords = 0,
    string? SampleProblem = null,
    Obstacle Obstacle = Obstacle.None);

/// <summary>The collaborators a session needs, so tests can run the whole flow without a browser.</summary>
public sealed record SessionEnvironment
{
    public required Func<UrlGuard, BrowserPageSourceOptions, CancellationToken, Task<IPageSource>> StartSource { get; init; }

    public required Func<string, string, string?> FetchRobots { get; init; }

    public required string BrowserProfileDirectory { get; init; }

    public IHostResolver? Resolver { get; init; }

    /// <summary>The real thing: Chromium with a persistent profile, and robots.txt over HTTP.</summary>
    public static SessionEnvironment Default(Action<string> warn) => new()
    {
        StartSource = static async (guard, options, token) =>
            await BrowserPageSource.StartAsync(guard, options, token).ConfigureAwait(false),
        FetchRobots = (url, agent) => RobotsHttp.Fetch(url, agent, warn),
        BrowserProfileDirectory = AppPaths.BrowserProfile,
    };
}

/// <summary>
/// The desktop flow: open a browser the person can use, let them sign in,
/// test the selectors, then extract. No UI code, so every step is testable.
/// </summary>
/// <remarks>
/// <para>
/// One browser per session, opened visibly with a persistent profile. The
/// whole reason the desktop app exists is the step where a person signs in or
/// solves a challenge themselves before anything is fetched, and a persistent
/// profile means they do it once rather than every run.
/// </para>
/// <para>
/// A robots.txt Disallow may be overridden only when the browser actually
/// carries cookies after that step, never merely because Ready was pressed.
/// That keeps the override a deliberate act rather than the default path.
/// </para>
/// </remarks>
public sealed class ExtractionSession(SessionEnvironment environment) : IAsyncDisposable
{
    private const int ExcerptCharacters = 600;

    private readonly Lock gate = new();
    private IPageSource? source;
    private UrlGuard? guard;
    private string? openedFor;
    private int concurrency;
    private SessionPhase phase = SessionPhase.Idle;

    public event EventHandler<SessionPhase>? PhaseChanged;

    public SessionPhase Phase
    {
        get
        {
            lock (this.gate)
            {
                return this.phase;
            }
        }
    }

    /// <summary>True once confirmed with cookies present: the evidence the robots override needs.</summary>
    public bool SessionAuthenticated { get; private set; }

    /// <summary>Open a visible browser on the contents page.</summary>
    public async Task LaunchAsync(SessionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.Require(SessionPhase.Idle);
        Refuse(settings.Problems().Where(problem => !problem.Contains("selector", StringComparison.Ordinal)).ToList());

        var tocUrl = settings.TocUrl.Trim();
        var guard = new UrlGuard(
            settings.AllowPrivateHosts,
            new CachingHostResolver(environment.Resolver ?? SystemHostResolver.Instance));
        var verdict = guard.Check(tocUrl);
        if (!verdict.Allowed)
        {
            throw new SessionException(
                $"That address cannot be used: {verdict.Reason?.ToWireValue()} {verdict.Detail}".Trim());
        }

        this.Move(SessionPhase.Launching);
        try
        {
            var options = new BrowserPageSourceOptions
            {
                Headless = false,
                UserDataDirectory = environment.BrowserProfileDirectory,
                UserAgent = settings.UserAgent == Robots.DefaultUserAgent ? null : settings.UserAgent,
                OperationBudget = settings.PageBudget,
                MaxPages = settings.Concurrency,
            };
            Directory.CreateDirectory(environment.BrowserProfileDirectory);
            this.source = await environment.StartSource(guard, options, cancellationToken).ConfigureAwait(false);
            this.guard = guard;
            this.concurrency = settings.Concurrency;
            this.openedFor = tocUrl;

            await this.source.OpenPageAsync(tocUrl, cancellationToken).ConfigureAwait(false);
            this.Move(SessionPhase.SigningIn);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await this.CloseAsync().ConfigureAwait(false);
            throw new SessionException(Explain(exception), exception);
        }
        catch
        {
            await this.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The person says they are done in the browser.</summary>
    /// <returns>Whether the browser carries a session, which is what permits a robots override.</returns>
    public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        this.Require(SessionPhase.SigningIn);
        this.SessionAuthenticated = await this.Source.HasSessionCookiesAsync(cancellationToken).ConfigureAwait(false);
        this.Move(SessionPhase.Ready);
        return this.SessionAuthenticated;
    }

    /// <summary>
    /// Read the contents page, report what the link selector found, and load
    /// the first chapter so the title and content selectors can be checked.
    /// Writes nothing.
    /// </summary>
    public async Task<SelectorPreview> PreviewAsync(SessionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.RequireReadyFor(settings);
        Refuse(settings.Problems());

        this.Move(SessionPhase.Previewing);
        try
        {
            var robots = this.PrepareRobots(settings, log: null);
            var options = settings.ToFetchOptions(this.SessionAuthenticated, dryRun: true);
            using var fetcher = new Fetcher(
                this.Source, this.Guard, new Core.Sinks.NullSink(), options, robots.Limiter, robots.Policy);

            CollectedLinks collected;
            try
            {
                collected = await fetcher.CollectAsync(settings.TocUrl.Trim(), settings.Selectors, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PageException exception)
            {
                throw new SessionException($"Could not read the contents page: {exception.Message}", exception);
            }

            var summary = Summarise(robots, settings.TocUrl.Trim());
            var kept = collected.Collection.Kept;
            var preview = new SelectorPreview(
                kept.Count,
                [.. kept.Take(5)],
                collected.Collection.ReasonCounts(),
                collected.Collection.Truncated,
                summary);

            if (kept.Count == 0)
            {
                var obstacle = await this.ObstacleOnAsync(settings, cancellationToken).ConfigureAwait(false);
                return preview with { SampleProblem = PageCheck.Advice(obstacle), Obstacle = obstacle };
            }

            await robots.Limiter.AcquireAsync(new Uri(kept[0]), cancellationToken).ConfigureAwait(false);
            try
            {
                var page = await this.Source.LoadChapterAsync(
                    kept[0], settings.TitleSelector, settings.ContentSelector, cancellationToken).ConfigureAwait(false);
                var text = TextCleaner.Clean(page.Body, !settings.IncludeLinks, settings.StripAds).Text;
                return preview with
                {
                    SampleTitle = page.Title.Trim(),
                    SampleExcerpt = Excerpt(text),
                    SampleWords = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                };
            }
            catch (PageException exception)
            {
                return preview with { SampleProblem = $"Chapter 1 could not be read: {exception.Message}" };
            }
        }
        finally
        {
            this.Move(SessionPhase.Ready);
        }
    }

    /// <summary>Fetch every chapter, reporting each as it lands. Cancel to stop; progress is kept.</summary>
    public async Task<PipelineResult> ExtractAsync(
        SessionSettings settings,
        IPipelineObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(observer);
        this.RequireReadyFor(settings);
        Refuse(settings.Problems());

        this.Move(SessionPhase.Extracting);
        try
        {
            var robots = this.PrepareRobots(settings, observer.Log);
            return await ExtractionPipeline.RunAsync(
                settings.ToRequest(this.SessionAuthenticated),
                this.Source,
                this.Guard,
                robots.Policy,
                robots.Limiter,
                observer,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.Move(SessionPhase.Ready);
        }
    }

    /// <summary>Mark that a stop was asked for. The caller cancels the token it passed.</summary>
    public void BeginStop()
    {
        lock (this.gate)
        {
            if (this.phase is not SessionPhase.Extracting and not SessionPhase.Previewing)
            {
                return;
            }
        }

        this.Move(SessionPhase.Stopping);
    }

    /// <summary>Close the browser and go back to the start.</summary>
    public async Task CloseAsync()
    {
        var closing = this.source;
        this.source = null;
        this.guard = null;
        this.openedFor = null;
        this.SessionAuthenticated = false;
        if (closing is not null)
        {
            await closing.DisposeAsync().ConfigureAwait(false);
        }

        this.Move(SessionPhase.Idle);
    }

    public async ValueTask DisposeAsync() => await this.CloseAsync().ConfigureAwait(false);

    internal static string Excerpt(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= ExcerptCharacters)
        {
            return trimmed;
        }

        var cut = trimmed.LastIndexOf(' ', ExcerptCharacters);
        return string.Concat(trimmed.AsSpan(0, cut > ExcerptCharacters / 2 ? cut : ExcerptCharacters), " ...");
    }

    /// <summary>Look at the contents page again, this time keeping its HTML, to see what is in the way.</summary>
    private async Task<Obstacle> ObstacleOnAsync(SessionSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var page = await this.Source.LoadTocAsync(
                settings.TocUrl.Trim(), settings.LinkSelector, captureHtml: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return PageCheck.Detect(page.Html);
        }
        catch (PageException)
        {
            return Obstacle.None;
        }
    }

    private IPageSource Source => this.source ?? throw new SessionException("The browser is not open.");

    private UrlGuard Guard => this.guard ?? throw new SessionException("The browser is not open.");

    private static RobotsSummary Summarise(RobotsSetup robots, string tocUrl) => new(
        robots.RobotsUrl,
        robots.Policy.Fetched,
        robots.Policy.CrawlDelay,
        robots.Policy.Rules.Count,
        robots.Policy.CanFetch(tocUrl));

    private static void Refuse(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0)
        {
            throw new SessionException(string.Join(Environment.NewLine, problems));
        }
    }

    private static string Explain(Exception exception) => exception switch
    {
        SessionException session => session.Message,
        PageException page => $"Could not open the contents page: {page.Message}",
        Microsoft.Playwright.PlaywrightException playwright when playwright.Message.Contains(
            "Executable doesn't exist", StringComparison.Ordinal) =>
            "The browser has not been downloaded yet. Restart the app to download it.",
        Microsoft.Playwright.PlaywrightException playwright when playwright.Message.Contains(
            "ProcessSingleton", StringComparison.Ordinal) =>
            "The browser from an earlier run is still open. Close it and try again.",
        _ => $"Could not start the browser: {exception.Message.Split('\n')[0]}",
    };

    private RobotsSetup PrepareRobots(SessionSettings settings, Action<string>? log) =>
        RobotsSetup.Prepare(
            settings.TocUrl.Trim(),
            settings.UserAgent,
            TimeSpan.FromSeconds(settings.MinDelaySeconds),
            environment.FetchRobots,
            log);

    private void RequireReadyFor(SessionSettings settings)
    {
        this.Require(SessionPhase.Ready);
        if (!string.Equals(settings.TocUrl.Trim(), this.openedFor, StringComparison.Ordinal))
        {
            throw new SessionException(
                "The contents page address changed since the browser opened. Close the browser and launch again.");
        }

        if (settings.Concurrency > this.concurrency)
        {
            throw new SessionException(
                $"The browser was opened for {this.concurrency} chapter(s) at once. Close it and launch again to use more.");
        }
    }

    private void Require(SessionPhase expected)
    {
        lock (this.gate)
        {
            if (this.phase != expected)
            {
                throw new SessionException($"Not possible right now ({this.phase}).");
            }
        }
    }

    private void Move(SessionPhase next)
    {
        lock (this.gate)
        {
            if (this.phase == next)
            {
                return;
            }

            this.phase = next;
        }

        this.PhaseChanged?.Invoke(this, next);
    }
}

/// <summary>A problem the person can act on, worded for them.</summary>
public sealed class SessionException : Exception
{
    public SessionException()
    {
    }

    public SessionException(string message)
        : base(message)
    {
    }

    public SessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

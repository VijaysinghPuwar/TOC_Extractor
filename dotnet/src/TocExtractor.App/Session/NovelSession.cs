using TocExtractor.App.Pipeline;
using TocExtractor.App.Scanning;
using TocExtractor.Browser;
using TocExtractor.Core.Fetching;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;

namespace TocExtractor.App.Session;

/// <summary>What a novel session needs, so tests can run it without a browser.</summary>
public sealed record NovelEnvironment
{
    public required Func<UrlGuard, BrowserPageSourceOptions, CancellationToken, Task<IPageSource>> StartSource { get; init; }

    public required Func<string, string, string?> FetchRobots { get; init; }

    public required string BrowserProfileDirectory { get; init; }

    public IHostResolver? Resolver { get; init; }

    public IPdfWriter? Pdf { get; init; }

    /// <summary>How long to wait for a person to pass a site's check before giving up on it.</summary>
    public TimeSpan PersonTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public static NovelEnvironment Default(Action<string> warn) => new()
    {
        StartSource = static async (guard, options, token) =>
            await BrowserPageSource.StartAsync(guard, options, token).ConfigureAwait(false),
        FetchRobots = (url, agent) => RobotsHttp.Fetch(url, agent, warn),
        BrowserProfileDirectory = AppPaths.BrowserProfile,
        Pdf = new ChromiumPdfWriter(),
    };
}

/// <summary>How a range will be saved, in words for the window.</summary>
public sealed record RangePreview(RangePlan Plan, int From, int To, string Summary, bool Slow);

/// <summary>
/// The desktop flow: scan a novel's page, choose a range, save it.
/// </summary>
/// <remarks>
/// <para>
/// One visible browser for the whole session, with a persistent profile, so
/// a sign-in or a passed check carries over to every later page and to the
/// next time the app is opened.
/// </para>
/// <para>
/// When a site shows its "verify you are human" page, the scan or download
/// pauses, <see cref="PersonNeeded"/> fires so the window can say so, and the
/// work carries on by itself once the person has passed it.
/// </para>
/// </remarks>
public sealed class NovelSession(NovelEnvironment environment) : INovelService
{
    /// <summary>Pages visited only to reach a range, above which the window asks first.</summary>
    public const int SlowWalkThreshold = 60;

    private IPageSource? source;
    private UrlGuard? guard;
    private RobotsSetup? robots;
    private string? origin;

    /// <summary>
    /// Raised when waiting for the person starts (with what they need to do)
    /// and again with null when it ends.
    /// </summary>
    public event EventHandler<string?>? PersonNeeded;

    public bool SignedIn { get; private set; }

    public SessionSettings Pace { get; set; } = new() { Concurrency = 1, MinDelaySeconds = 2, MaxDelaySeconds = 4 };

    /// <summary>Find the book's chapters from its page. Opens the browser the first time.</summary>
    public async Task<ScanResult> ScanAsync(string novelUrl, Action<string>? log, CancellationToken cancellationToken = default)
    {
        var url = Normalise(novelUrl);
        var source = await this.EnsureSourceAsync(url, cancellationToken).ConfigureAwait(false);
        this.robots = this.PrepareRobots(url, log);

        var scanner = new NovelScanner(
            (IPageProbe)source, this.guard!, this.robots.Policy, this.robots.Limiter,
            this.SignedIn, log, this.WaitForPersonAsync);
        var scan = await scanner.ScanAsync(url, cancellationToken).ConfigureAwait(false);

        // A site that lists its chapters but closes the chapter pages
        // themselves to tools: only a signed-in reader can go on.
        if (scan.Ready && !this.SignedIn && scan.Chapters.Count > 0
            && !this.robots.Policy.CanFetch(scan.Chapters[0].Url))
        {
            return scan with
            {
                Problem = SignInNeeded(this.robots.Policy.MatchedRule(scan.Chapters[0].Url)?.Describe()),
                Obstacle = Obstacle.SignIn,
            };
        }

        return scan;
    }

    /// <summary>Plan a range and describe it, with the cost of any walking.</summary>
    public RangePreview Preview(ScanResult scan, int first, int last)
    {
        var (from, to) = (first, last);
        ArgumentNullException.ThrowIfNull(scan);
        var plan = RangePlanner.Plan(scan, from, to);
        var low = Math.Max(Math.Min(from, to), scan.FirstNumber);
        var high = Math.Min(Math.Max(from, to), scan.LastNumber);
        var count = Math.Max(0, high - low + 1);
        if (plan.Problem is not null)
        {
            return new RangePreview(plan, low, high, plan.Problem, false);
        }

        var seconds = (plan.Requested + plan.ExtraVisits) * (this.Pace.MinDelaySeconds + this.Pace.MaxDelaySeconds) / 2
            / Math.Max(1, this.Pace.Concurrency);
        var time = seconds < 90 ? $"about {Math.Max(1, (int)Math.Round(seconds / 60))} min" : $"about {(int)Math.Round(seconds / 60)} min";
        var how = plan.Walks.Count == 0
            ? "each opened directly"
            : plan.ExtraVisits == 0
                ? "some reached by following the site's next and previous links"
                : $"{plan.ExtraVisits} extra page(s) visited to reach them, because the site lists no closer chapter";
        return new RangePreview(plan, low, high, $"{count} chapter(s), {how}. {time}.", plan.ExtraVisits > SlowWalkThreshold);
    }

    /// <summary>
    /// Save a planned range. Built addresses are checked on one chapter first;
    /// if the pattern does not hold, the range is planned again without it.
    /// </summary>
    public async Task<RangeResult> SaveAsync(
        ScanResult scan,
        RangePreview preview,
        string outputRoot,
        bool text,
        bool pdf,
        bool csvLog,
        bool force,
        IPipelineObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(observer);
        var source = this.source ?? throw new SessionException("Scan the novel first.");
        var robots = this.robots ?? throw new SessionException("Scan the novel first.");

        var plan = preview.Plan;
        if (plan.PredictedLinks.Count > 0 && !await this.PredictionHoldsAsync(plan.PredictedLinks[0], observer, cancellationToken).ConfigureAwait(false))
        {
            observer.Log("The address pattern did not hold on the site, so chapters are reached by following links instead.");
            plan = RangePlanner.Plan(scan, preview.From, preview.To, predict: false);
        }

        var request = new RangeRequest
        {
            Scan = scan,
            Plan = plan,
            From = preview.From,
            To = preview.To,
            OutputRoot = outputRoot,
            WriteText = text,
            WritePdf = pdf,
            Force = force,
            Fetch = this.Pace.ToFetchOptions(this.SignedIn),
        };

        CsvLog? log = csvLog ? new CsvLog(CsvLog.NewPath(request.BookDirectory, DateTimeOffset.Now), observer) : null;
        try
        {
            return await RangePipeline.RunAsync(
                request, source, this.guard!, robots.Policy, robots.Limiter, (IPipelineObserver?)log ?? observer,
                environment.Pdf, this.WaitForPersonAsync, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            log?.Dispose();
        }
    }

    /// <summary>Open the site so the person can sign in. The window is theirs until <see cref="FinishSignInAsync"/>.</summary>
    public async Task BeginSignInAsync(string novelUrl, CancellationToken cancellationToken = default)
    {
        var url = Normalise(novelUrl);
        var source = await this.EnsureSourceAsync(url, cancellationToken).ConfigureAwait(false);
        if (source is BrowserPageSource browser)
        {
            browser.PersonAtWindow = true;
        }

        await source.OpenPageAsync(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The person says they are done. Returns whether the browser now carries an account.</summary>
    public async Task<bool> FinishSignInAsync(CancellationToken cancellationToken = default)
    {
        if (this.source is null)
        {
            return false;
        }

        if (this.source is BrowserPageSource browser)
        {
            browser.PersonAtWindow = false;
        }

        this.SignedIn = await this.source.HasSessionCookiesAsync(cancellationToken).ConfigureAwait(false);
        return this.SignedIn;
    }

    public async Task CloseAsync()
    {
        var closing = this.source;
        this.source = null;
        this.origin = null;
        if (closing is not null)
        {
            await closing.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await this.CloseAsync().ConfigureAwait(false);

    public static string SignInNeeded(string? rule) =>
        "Sign in required. This site only lets signed-in readers' tools open chapters"
        + (rule is null ? "" : $" ({rule})")
        + ". Press Sign in, sign in in the browser window, then press Done and scan again.";

    private static string Normalise(string url)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SessionException("Paste the novel's page address, starting with https://");
        }

        return uri.ToString();
    }

    private async Task<IPageSource> EnsureSourceAsync(string url, CancellationToken cancellationToken)
    {
        var host = new Uri(url).GetLeftPart(UriPartial.Authority);
        if (this.source is not null && this.origin == host)
        {
            return this.source;
        }

        await this.CloseAsync().ConfigureAwait(false);
        var guard = new UrlGuard(false, new CachingHostResolver(environment.Resolver ?? SystemHostResolver.Instance));
        var verdict = guard.Check(url);
        if (!verdict.Allowed)
        {
            throw new SessionException($"That address cannot be used: {verdict.Reason?.ToWireValue()} {verdict.Detail}".Trim());
        }

        Directory.CreateDirectory(environment.BrowserProfileDirectory);
        var options = new BrowserPageSourceOptions
        {
            Headless = false,
            UserDataDirectory = environment.BrowserProfileDirectory,
            OperationBudget = this.Pace.PageBudget,
            MaxPages = Math.Max(1, this.Pace.Concurrency),
        };

        try
        {
            this.source = await environment.StartSource(guard, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Playwright.PlaywrightException exception)
        {
            throw new SessionException(
                exception.Message.Contains("ProcessSingleton", StringComparison.Ordinal)
                    ? "The browser from an earlier run is still open. Close it and try again."
                    : $"Could not start the browser: {exception.Message.Split('\n')[0]}",
                exception);
        }

        this.guard = guard;
        this.origin = host;
        this.SignedIn = await this.source.HasSessionCookiesAsync(cancellationToken).ConfigureAwait(false);
        return this.source;
    }

    private RobotsSetup PrepareRobots(string url, Action<string>? log) =>
        RobotsSetup.Prepare(url, Robots.DefaultUserAgent, TimeSpan.FromSeconds(this.Pace.MinDelaySeconds), environment.FetchRobots, log);

    private async Task<bool> PredictionHoldsAsync(NumberedLink link, IPipelineObserver observer, CancellationToken cancellationToken)
    {
        if (this.source is not IPageProbe probe)
        {
            return true;
        }

        try
        {
            var (_, json) = await probe.ProbeAsync(
                link.Url, "() => JSON.stringify({ title: document.title })", TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
            var title = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("title").GetString();
            var holds = ChapterNumbers.FromTitle(title) == link.Number;
            observer.Log(holds
                ? $"Checked: chapter {link.Number} is at its predicted address."
                : $"Chapter {link.Number}'s predicted address holds \"{title}\".");
            return holds;
        }
        catch (PageException exception)
        {
            observer.Log($"Could not check the predicted address for chapter {link.Number}: {exception.Message}");
            return false;
        }
    }

    internal const string CheckAdvice =
        "The site wants to check you're a person. Complete it in the browser window; saving carries on by itself.";

    internal const string SignInAdvice =
        "This site shows only part of each chapter unless you're signed in. Sign in in the browser window; saving carries on by itself.";

    private async Task<bool> WaitForPersonAsync(HumanCheckException check, CancellationToken cancellationToken)
    {
        if (this.source is not IHumanGate gate)
        {
            return false;
        }

        this.PersonNeeded?.Invoke(this, check.NeedsSignIn ? SignInAdvice : CheckAdvice);
        try
        {
            var passed = check.NeedsSignIn
                ? await gate.WaitForSignInAsync(environment.PersonTimeout, cancellationToken).ConfigureAwait(false)
                : await gate.WaitForPersonAsync(environment.PersonTimeout, cancellationToken).ConfigureAwait(false);
            if (check.NeedsSignIn && passed)
            {
                this.SignedIn = true;
            }

            return passed;
        }
        finally
        {
            this.PersonNeeded?.Invoke(this, null);
        }
    }
}

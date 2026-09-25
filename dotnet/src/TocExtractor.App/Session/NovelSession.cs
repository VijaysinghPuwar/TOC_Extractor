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
/// One extraction in the desktop app: scan a novel's page, choose a range, save it.
/// </summary>
/// <remarks>
/// <para>
/// Every extraction shares one visible browser (a <see cref="BrowserHost"/>),
/// with a persistent profile, so a sign-in or a passed check carries over to
/// every later page, to the other extractions, and to the next time the app
/// is opened. Each session keeps its own site, robots.txt answer and
/// progress, so several run side by side.
/// </para>
/// <para>
/// When a site shows its "verify you are human" page, the scan or download
/// pauses, <see cref="PersonNeeded"/> fires so the window can say so, and the
/// work carries on by itself once the person has passed it.
/// </para>
/// </remarks>
public sealed class NovelSession : INovelService
{
    /// <summary>Pages visited only to reach a range, above which the window asks first.</summary>
    public const int SlowWalkThreshold = 60;

    private readonly BrowserHost host;
    private readonly bool ownsHost;
    private readonly NovelEnvironment environment;
    private IPageSource? source;
    private Action<string>? report;
    private RobotsSetup? robots;
    private string? origin;
    private string? siteUrl;
    private int signingIn;

    /// <summary>A session with a browser of its own.</summary>
    public NovelSession(NovelEnvironment environment)
        : this(new BrowserHost(environment), ownsHost: true)
    {
    }

    /// <summary>A session sharing <paramref name="host"/>'s browser with other sessions.</summary>
    public NovelSession(BrowserHost host)
        : this(host, ownsHost: false)
    {
    }

    private NovelSession(BrowserHost host, bool ownsHost)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.ownsHost = ownsHost;
        this.environment = host.Environment;
    }

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
        this.report = log;

        var scanner = new NovelScanner(
            (IPageProbe)source, this.host.Guard, this.robots.Policy, this.robots.Limiter,
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
        bool force,
        IPipelineObserver observer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(observer);
        var robots = this.robots ?? throw new SessionException("Scan the novel first.");
        if (this.source is null)
        {
            throw new SessionException("Scan the novel first.");
        }

        // The browser may have been closed since the scan; this reopens it.
        var source = await this.EnsureSourceAsync(this.siteUrl!, cancellationToken).ConfigureAwait(false);

        this.report = observer.Log;
        var plan = preview.Plan;
        if (plan.PredictedLinks.Count > 0 && !await this.PredictionHoldsAsync(plan.PredictedLinks[0], observer, cancellationToken).ConfigureAwait(false))
        {
            var walking = RangePlanner.Plan(scan, preview.From, preview.To, predict: false);
            if (walking.ExtraVisits > SlowWalkThreshold)
            {
                // Never swap a direct plan for a long walk without asking:
                // hundreds of extra pages are what make a site start checking.
                throw new SessionException(
                    $"The site's chapter addresses do not follow the pattern the scan found. Reaching chapter {preview.From} "
                    + $"would mean visiting {walking.ExtraVisits} other chapters first. Scan again, or choose chapters nearer to ones the site lists.");
            }

            observer.Log("The address pattern did not hold on the site, so chapters are reached by following links instead.");
            plan = walking;
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
            TextHeadings = this.Pace.BookHeadings,
            ForSpeech = this.Pace.ForSpeech,
            Force = force,
            Fetch = this.Pace.ToFetchOptions(this.SignedIn),
        };

        var formats = text && pdf ? "TXT and PDF" : pdf ? "PDF" : "TXT";
        var pace = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{this.Pace.Concurrency} at once, {this.Pace.MinDelaySeconds:0.#}-{this.Pace.MaxDelaySeconds:0.#}s between pages");
        observer.Log($"Saving chapters {preview.From}-{preview.To} of \"{scan.BookTitle}\" to {request.BookDirectory}: {formats}, {pace}"
            + (this.SignedIn ? ", signed in" : "") + (force ? ", starting over" : "") + ".");
        return await RangePipeline.RunAsync(
            request, source, this.host.Guard, robots.Policy, robots.Limiter, observer,
            this.environment.Pdf, this.WaitForPersonAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Open the site so the person can sign in. The window is theirs until <see cref="FinishSignInAsync"/>.</summary>
    public async Task BeginSignInAsync(string novelUrl, CancellationToken cancellationToken = default)
    {
        var url = Normalise(novelUrl);
        var source = await this.EnsureSourceAsync(url, cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref this.signingIn, 1) == 0)
        {
            this.host.PersonArrived();
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

        this.EndSignIn();
        this.SignedIn = await this.source.HasSessionCookiesAsync(this.siteUrl, cancellationToken).ConfigureAwait(false);
        return this.SignedIn;
    }

    /// <summary>
    /// Let go of the browser. The shared browser stays open for the other
    /// extractions; a session that owns its browser closes it.
    /// </summary>
    public async Task CloseAsync()
    {
        this.EndSignIn();
        this.source = null;
        this.origin = null;
        if (this.ownsHost)
        {
            await this.host.CloseAsync().ConfigureAwait(false);
        }
    }

    private void EndSignIn()
    {
        if (Interlocked.Exchange(ref this.signingIn, 0) == 1)
        {
            this.host.PersonLeft();
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
        var verdict = this.host.Guard.Check(url);
        if (!verdict.Allowed)
        {
            throw new SessionException($"That address cannot be used: {verdict.Reason?.ToWireValue()} {verdict.Detail}".Trim());
        }

        var site = new Uri(url).GetLeftPart(UriPartial.Authority);
        this.siteUrl = url;
        var source = await this.host.SourceAsync(this.Pace.PageBudget, cancellationToken).ConfigureAwait(false);
        if (this.source is null || this.origin != site)
        {
            // A new site: the sign-in has to be looked for again.
            this.source = source;
            this.origin = site;
            this.SignedIn = await source.HasSessionCookiesAsync(url, cancellationToken).ConfigureAwait(false);
        }

        return source;
    }

    private RobotsSetup PrepareRobots(string url, Action<string>? log)
    {
        var minDelay = TimeSpan.FromSeconds(this.Pace.MinDelaySeconds);
        return RobotsSetup.Prepare(
            url, Robots.DefaultUserAgent, minDelay, this.environment.FetchRobots, log, this.host.LimiterFor(url, minDelay));
    }

    private async Task<bool> PredictionHoldsAsync(NumberedLink link, IPipelineObserver observer, CancellationToken cancellationToken)
    {
        if (this.source is not IPageProbe probe)
        {
            return true;
        }

        const string Script = "() => JSON.stringify({ title: document.title })";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var (_, json) = await probe.ProbeAsync(link.Url, Script, TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
                var title = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("title").GetString();
                var number = ChapterNumbers.FromTitle(title);

                // Only a page that is clearly another chapter disproves the
                // pattern; a title with no number in it says nothing either way.
                var holds = number is null || number == link.Number;
                observer.Log(holds
                    ? $"Checked: chapter {link.Number} is at its predicted address."
                    : $"Chapter {link.Number}'s predicted address holds \"{title}\".");
                return holds;
            }
            catch (HumanCheckException check)
            {
                // The site is checking for a person, which says nothing about
                // the pattern. Wait for them, then look again.
                if (!await this.WaitForPersonAsync(check, cancellationToken).ConfigureAwait(false))
                {
                    throw new SessionException("The site's check was not completed, so saving did not start.");
                }
            }
            catch (PageException exception)
            {
                observer.Log($"Could not check the predicted address for chapter {link.Number}: {exception.Message}. Going ahead with it; each chapter is still checked as it is saved.");
                return true;
            }
        }

        return true;
    }

    /// <summary>The longest wait between pages the app slows itself to.</summary>
    public static readonly TimeSpan SlowestPace = TimeSpan.FromSeconds(15);

    /// <summary>The wait between pages now, which checks raise.</summary>
    public TimeSpan CurrentInterval { get; private set; }

    /// <summary>
    /// A site that asked once will ask again at the same pace, so every check
    /// the person passes makes the wait between pages half as long again.
    /// </summary>
    private void SlowDown()
    {
        if (this.robots is null || this.origin is null)
        {
            return;
        }

        var host = new Uri(this.origin);
        var now = this.robots.Limiter.IntervalFor(host);
        var slower = TimeSpan.FromSeconds(Math.Min(SlowestPace.TotalSeconds, Math.Max(now.TotalSeconds, this.Pace.MinDelaySeconds) * 1.5));
        this.robots.Limiter.SetHostInterval(host, slower);
        this.CurrentInterval = slower;
        this.report?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"The site asked for a check, so the app now waits {slower.TotalSeconds:0.#}s between pages to make another less likely."));
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
            this.report?.Invoke(check.NeedsSignIn
                ? "The site shows only part of the chapter unless you are signed in; waiting for you to sign in in the browser."
                : "The site asked to check you are a person; waiting for you in the browser.");
            var passed = check.NeedsSignIn
                ? await gate.WaitForSignInAsync(this.siteUrl, this.environment.PersonTimeout, cancellationToken).ConfigureAwait(false)
                : await gate.WaitForPersonAsync(this.environment.PersonTimeout, cancellationToken).ConfigureAwait(false);
            this.report?.Invoke(passed ? "Done in the browser; carrying on." : "Nobody completed the check in time.");
            if (passed && !check.NeedsSignIn)
            {
                this.SlowDown();
            }
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

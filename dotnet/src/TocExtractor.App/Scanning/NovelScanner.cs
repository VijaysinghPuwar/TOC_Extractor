using System.Reflection;
using System.Text.Json;
using TocExtractor.App.Session;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;

namespace TocExtractor.App.Scanning;

/// <summary>
/// Works out, from nothing but a novel's page, where its chapters are and
/// where the story sits on a chapter page.
/// </summary>
/// <remarks>
/// <para>
/// The scan reads the page the way a person would: finds the list of
/// chapters, follows an "all chapters" page if there is one, and reads further
/// list pages while they keep adding chapters. Then it opens the first
/// chapter, finds the story, title and next/previous links, and checks those
/// on a second chapter, so a selector that only happened to fit one page is
/// caught before a download relies on it.
/// </para>
/// <para>
/// Every page it reads goes through the same rules as a download: the URL
/// guard, robots.txt, and the per-host delay. A list page robots.txt closes
/// is not read, and the scan says so, because the download can often reach
/// those chapters another way (walking next or previous links).
/// </para>
/// </remarks>
public sealed class NovelScanner(
    IPageProbe probe,
    UrlGuard guard,
    RobotsPolicy robots,
    RateLimiter limiter,
    bool sessionAuthenticated = false,
    Action<string>? log = null,
    Func<string, CancellationToken, Task<bool>>? onHumanCheck = null)
{
    /// <summary>A ceiling on list pages read, against a site whose pages never end.</summary>
    public const int MaxListPages = 300;

    /// <summary>List pages in a row that add nothing before paging stops.</summary>
    private const int UnproductiveLimit = 2;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string FindChapters = Script("find-chapters.js");
    private static readonly string FindContent = Script("find-content.js");

    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private readonly List<string> notes = [];

    // Keyed by address, not number: a site can give two chapters one number.
    private readonly Dictionary<string, ChapterLink> links = new(StringComparer.Ordinal);
    private string? locked;
    private List<ScannedChapter> chapters = [];

    public async Task<ScanResult> ScanAsync(string novelUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(novelUrl);

        var first = await this.ReadListAsync(novelUrl, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return this.Result(novelUrl, "", problem: "The novel page could not be read. See the activity log for why.");
        }

        if (first.Probe.Challenge || first.Probe.SignIn)
        {
            var obstacle = first.Probe.Challenge ? Obstacle.HumanCheck : Obstacle.SignIn;
            return this.Result(novelUrl, first.Probe.Title, PageCheck.Advice(obstacle), obstacle);
        }

        var title = first.Probe.Title;
        List<ListPage> read = [first];

        // An "all chapters" page, when the novel page shows only some.
        foreach (var list in first.Probe.Lists
                     .DistinctBy(l => Normalise(l.Url), StringComparer.Ordinal)
                     .Where(l => !this.visited.Contains(Normalise(l.Url)))
                     .OrderByDescending(l => l.Text.Contains("chapter", StringComparison.OrdinalIgnoreCase) || l.Url.Contains("chapter", StringComparison.OrdinalIgnoreCase))
                     .Take(2))
        {
            var page = await this.ReadListAsync(list.Url, cancellationToken).ConfigureAwait(false);
            if (page is not null)
            {
                read.Add(page);
                this.notes.Add($"Read the chapter list at {list.Url}.");
            }
        }

        // Lock onto this book's chapter addresses: the pattern of the page
        // that listed the most. From here on, only links following it count.
        this.locked = read.Select(page => page.Probe)
            .Where(probe => probe.Key is not null)
            .OrderByDescending(probe => probe.Chapters.Count)
            .Select(probe => probe.Key)
            .FirstOrDefault();
        foreach (var page in read)
        {
            this.Merge(page.Probe);
        }

        await this.ReadListPagesAsync(read, cancellationToken).ConfigureAwait(false);
        await this.ReadFirstChapterAsync(read, cancellationToken).ConfigureAwait(false);
        this.chapters = this.Order();

        if (this.chapters.Count == 0)
        {
            return this.Result(
                novelUrl,
                title,
                "No chapter list was found on this page. Open the page that lists the chapters and scan that, or set the selectors under Advanced.");
        }

        var layout = await this.FindLayoutAsync(cancellationToken).ConfigureAwait(false);
        return layout.Layout is null
            ? this.Result(novelUrl, title, layout.Problem)
            : this.Result(novelUrl, title, problem: null) with { Layout = layout.Layout };
    }

    /// <summary>Probe a page; if the site asks for a person, wait for them and try once more.</summary>
    private async Task<(string FinalUrl, string Json)> ProbeOnceMoreAfterCheckAsync(
        string url, string script, TimeSpan settle, CancellationToken cancellationToken)
    {
        try
        {
            return await probe.ProbeAsync(url, script, settle, cancellationToken).ConfigureAwait(false);
        }
        catch (HumanCheckException) when (onHumanCheck is not null)
        {
            log?.Invoke($"scan: {url} asked to check you are human; waiting for you in the browser");
            if (!await onHumanCheck(url, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            return await probe.ProbeAsync(url, script, settle, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Script(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded script {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Normalise(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Query) : url;

    /// <summary>Read further numbered list pages, pattern by pattern, while they keep adding chapters.</summary>
    private async Task ReadListPagesAsync(List<ListPage> read, CancellationToken cancellationToken)
    {
        var patterns = read
            .SelectMany(page => page.Probe.Pages)
            .GroupBy(p => p.Pattern, StringComparer.Ordinal)
            .Where(g => g.Select(p => p.Url).Distinct(StringComparer.Ordinal).Count() >= 2
                || g.Any(p => !p.Text.All(char.IsDigit)))
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => g.Key)
            .ToList();

        var budget = MaxListPages;
        foreach (var pattern in patterns)
        {
            Queue<string> queue = new(read
                .SelectMany(page => page.Probe.Pages)
                .Where(p => p.Pattern == pattern)
                .Select(p => p.Url)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(PageNumber));
            var unproductive = 0;
            var any = false;
            while (queue.Count > 0 && budget > 0 && unproductive < UnproductiveLimit)
            {
                var url = queue.Dequeue();
                if (this.visited.Contains(Normalise(url)))
                {
                    continue;
                }

                if (!this.Permitted(url, out var reason))
                {
                    this.notes.Add($"Did not read list pages like {url}: {reason}. Chapters they would list can still be reached from neighbouring chapters.");
                    break;
                }

                budget--;
                var before = this.links.Count;
                var page = await this.ReadListAsync(url, cancellationToken).ConfigureAwait(false);
                if (page is null)
                {
                    unproductive++;
                    continue;
                }

                any = true;
                unproductive = this.links.Count > before ? 0 : unproductive + 1;
                foreach (var more in page.Probe.Pages.Where(p => p.Pattern == pattern))
                {
                    if (!this.visited.Contains(Normalise(more.Url)) && !queue.Contains(more.Url))
                    {
                        queue.Enqueue(more.Url);
                    }
                }
            }

            if (any)
            {
                this.notes.Add($"Read {MaxListPages - budget} list page(s).");
            }
        }
    }

    private static int PageNumber(string url)
    {
        var digits = new string([.. url.Reverse().SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).Reverse()]);
        return int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }

    private bool Permitted(string url, out string reason)
    {
        var verdict = guard.Check(url);
        if (!verdict.Allowed)
        {
            reason = "the address is not allowed";
            return false;
        }

        if (!robots.CanFetch(url) && !sessionAuthenticated)
        {
            reason = "robots.txt disallows them";
            return false;
        }

        reason = "";
        return true;
    }

    private async Task<ListPage?> ReadListAsync(string url, CancellationToken cancellationToken)
    {
        this.visited.Add(Normalise(url));
        if (!this.Permitted(url, out var reason))
        {
            log?.Invoke($"scan: skipped {url}: {reason}");
            this.notes.Add($"Did not read {url}: {reason}.");
            return null;
        }

        try
        {
            await limiter.AcquireAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            var script = this.locked is null
                ? FindChapters
                : "() => { window.__tocExtractorKey = " + JsonSerializer.Serialize(this.locked) + "; return (" + FindChapters.Trim() + ")(); }";
            var (finalUrl, json) = await this.ProbeOnceMoreAfterCheckAsync(url, script, TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);
            this.visited.Add(Normalise(finalUrl));
            var found = JsonSerializer.Deserialize<ListProbe>(json, Json) ?? new ListProbe();
            var added = this.locked is null ? 0 : this.Merge(found);
            log?.Invoke($"scan: read {url}: {found.Chapters.Count} chapter link(s)"
                + (this.locked is null ? "" : $", {added} new"));
            return new ListPage(url, found);
        }
        catch (PageException exception)
        {
            log?.Invoke($"scan: could not read {url}: {exception.Message}");
            this.notes.Add($"Could not read {url}: {exception.Message}");
            return null;
        }
    }

    /// <summary>Add a page's chapter links that follow this book's pattern. Returns how many were new.</summary>
    private int Merge(ListProbe found)
    {
        var added = 0;
        foreach (var chapter in found.Chapters)
        {
            if (chapter.Number > 0 && (this.locked is null || chapter.Key == this.locked)
                && this.links.TryAdd(chapter.Url, chapter))
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Follow a "Start reading" or "First chapter" link when chapter 1 is not
    /// listed: newest-first lists often never show it, and it is the anchor
    /// every early range is reached from.
    /// </summary>
    private async Task ReadFirstChapterAsync(List<ListPage> read, CancellationToken cancellationToken)
    {
        if (this.links.Values.Any(link => link.Number == 1))
        {
            return;
        }

        var first = read.SelectMany(page => page.Probe.Firsts).Select(link => link.Url).FirstOrDefault();
        if (first is null)
        {
            return;
        }

        var found = await this.ProbeContentAsync(first, FindContent, cancellationToken, keepUrl: true).ConfigureAwait(false);
        var landed = found?.Canonical is { } canonical && Uri.TryCreate(canonical, UriKind.Absolute, out _)
            ? canonical
            : found?.FinalUrl;
        if (landed is { } url && (ChapterNumbers.FromTitle(found!.TitleText) ?? 1) == 1)
        {
            this.links.TryAdd(url, new ChapterLink(url, 1, found.TitleText ?? "Chapter 1", this.locked));
            this.notes.Add("Found chapter 1 through the site's first-chapter link.");
        }
    }

    /// <summary>
    /// Put the chapters in reading order and give each a number.
    /// </summary>
    /// <remarks>
    /// Titles are not always right: a site can label two chapters with one
    /// number, or skip one. When every address ends in an id and the ids agree
    /// with the titles for nearly every neighbour, the ids are the reading
    /// order, and a chapter whose label goes backwards is numbered by its
    /// place instead. Otherwise the titles are the order and the first of
    /// two same-numbered chapters is kept.
    /// </remarks>
    private List<ScannedChapter> Order()
    {
        var all = this.links.Values.ToList();
        var ids = all.Select(link => TrailingId(link.Url)).ToList();
        if (all.Count >= 3 && ids.All(id => id is not null) && ids.Distinct().Count() == ids.Count)
        {
            var byId = all.Zip(ids, (link, id) => (link, id: id!.Value)).OrderBy(pair => pair.id).Select(pair => pair.link).ToList();
            var agreeing = byId.Zip(byId.Skip(1), (a, b) => b.Number > a.Number).Count(ok => ok);
            if (agreeing >= 0.9 * (byId.Count - 1))
            {
                List<ScannedChapter> ordered = [];
                List<string> renumbered = [];
                var previous = 0;
                foreach (var link in byId)
                {
                    var number = link.Number > previous ? link.Number : previous + 1;
                    if (number != link.Number)
                    {
                        renumbered.Add($"\"{Short(link.Title)}\" is numbered {number} by its place (the site says {link.Number})");
                    }

                    ordered.Add(new ScannedChapter(number, link.Title, link.Url));
                    previous = number;
                }

                if (renumbered.Count > 0)
                {
                    this.notes.Add("Some chapters repeat an earlier number on the site: " + string.Join("; ", renumbered) + ".");
                }

                return ordered;
            }
        }

        return [.. all.GroupBy(link => link.Number).Select(group => group.First()).OrderBy(link => link.Number)
            .Select(link => new ScannedChapter(link.Number, link.Title, link.Url))];
    }

    private static string Short(string title) => title.Length <= 50 ? title : title[..50] + "...";

    internal static long? TrailingId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var end = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? path.Length - 5
            : path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ? path.Length - 4 : path.Length;
        var start = end;
        while (start > 0 && char.IsDigit(path[start - 1]))
        {
            start--;
        }

        return end - start is > 0 and <= 12 && long.TryParse(path.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    /// <summary>Find the chapter page's parts on one chapter, and confirm them on another.</summary>
    private async Task<(ChapterLayout? Layout, string Problem)> FindLayoutAsync(CancellationToken cancellationToken)
    {
        // Found on a middle chapter, which has both a next and a previous
        // link, and confirmed on the first, whose previous link goes nowhere.
        var ordered = this.chapters;
        var sample = ordered[ordered.Count / 2];
        var check = ordered.Count > 1 ? ordered[0] : null;

        var found = await this.ProbeContentAsync(sample.Url, FindContent, cancellationToken).ConfigureAwait(false);
        if (found is null || found.Content is null || found.ContentChars < 200)
        {
            return (null, $"Could not find the story text on chapter {sample.Number}. Set the selectors under Advanced.");
        }

        if (found.Challenge)
        {
            return (null, PageCheck.Advice(Obstacle.HumanCheck));
        }

        if (check is not null)
        {
            var confirm = await this.ProbeContentAsync(check.Url, Confirm(found), cancellationToken).ConfigureAwait(false);
            if (confirm is null || confirm.ContentChars < 200)
            {
                return (null, $"The story text was found on chapter {sample.Number} but not on chapter {check.Number}. Set the selectors under Advanced.");
            }
        }

        this.notes.Add($"Story text is in {found.Content}, the title in {found.Title ?? "the page title"}.");
        return (new ChapterLayout(found.Title ?? "title", found.Content, found.Next, found.Prev), "");
    }

    /// <summary>A script that measures the found selectors on another chapter.</summary>
    private static string Confirm(ContentProbe found) =>
        "() => { const el = document.querySelector(" + JsonSerializer.Serialize(found.Content) + ");"
        + " return JSON.stringify({ content: " + JsonSerializer.Serialize(found.Content)
        + ", contentChars: el ? (el.innerText || '').trim().length : 0 }); }";

    private async Task<ContentProbe?> ProbeContentAsync(
        string url, string script, CancellationToken cancellationToken, bool keepUrl = false)
    {
        if (!this.Permitted(url, out var reason))
        {
            this.notes.Add($"Could not open chapter page {url}: {reason}.");
            return null;
        }

        try
        {
            await limiter.AcquireAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            var (finalUrl, json) = await this.ProbeOnceMoreAfterCheckAsync(url, script, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            log?.Invoke($"scan: examined chapter page {url}");
            var found = JsonSerializer.Deserialize<ContentProbe>(json, Json);
            return keepUrl && found is not null ? found with { FinalUrl = finalUrl } : found;
        }
        catch (PageException exception)
        {
            log?.Invoke($"scan: could not read {url}: {exception.Message}");
            return null;
        }
    }

    private ScanResult Result(string novelUrl, string title, string? problem, Obstacle obstacle = Obstacle.None) => new()
    {
        NovelUrl = novelUrl,
        BookTitle = title,
        Chapters = [.. this.chapters],
        Notes = [.. this.notes],
        Problem = problem,
        Obstacle = obstacle,
        PagesRead = this.visited.Count,
    };

    private sealed record ListPage(string Url, ListProbe Probe);

    internal sealed record ListProbe
    {
        public string Title { get; init; } = "";

        public List<ChapterLink> Chapters { get; init; } = [];

        public List<Link> Lists { get; init; } = [];

        public List<PageLink> Pages { get; init; } = [];

        public List<Link> Firsts { get; init; } = [];

        public string? Key { get; init; }

        public bool Challenge { get; init; }

        public bool SignIn { get; init; }
    }

    internal sealed record ChapterLink(string Url, int Number, string Title, string? Key = null);

    internal sealed record Link(string Url, string Text);

    internal sealed record PageLink(string Url, string Text, string Pattern);

    internal sealed record ContentProbe
    {
        public string? Content { get; init; }

        public int ContentChars { get; init; }

        public string? Title { get; init; }

        public string? TitleText { get; init; }

        public string? FinalUrl { get; init; }

        public string? Canonical { get; init; }

        public string? Next { get; init; }

        public string? Prev { get; init; }

        public bool Challenge { get; init; }
    }
}

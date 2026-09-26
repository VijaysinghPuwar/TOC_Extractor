using TocExtractor.Core.Fetching;

namespace TocExtractor.App.Scanning;

/// <summary>How a chosen range of chapters will be reached.</summary>
/// <param name="Direct">Chapters whose addresses the scan found.</param>
/// <param name="Walks">Stretches no list shows, reached from a neighbour.</param>
/// <param name="ExtraVisits">Pages visited only to get somewhere, outside the range.</param>
/// <param name="Problem">Why the range cannot be fetched, or null.</param>
/// <param name="Predicted">Direct links built from the book's address pattern rather than found on a page.</param>
public sealed record RangePlan(
    IReadOnlyList<NumberedLink> Direct,
    IReadOnlyList<WalkSpec> Walks,
    int ExtraVisits,
    string? Problem,
    IReadOnlyList<NumberedLink>? Predicted = null)
{
    public int Requested => this.Direct.Count + this.Walks.Sum(w => w.SaveTo - w.SaveFrom + 1);

    public IReadOnlyList<NumberedLink> PredictedLinks => this.Predicted ?? [];
}

/// <summary>
/// Plans the cheapest way to fetch chapters <c>from</c> to <c>to</c>.
/// </summary>
/// <remarks>
/// Chapters with a known address are fetched directly. Each stretch without
/// one is walked from whichever known neighbour needs fewer pages outside the
/// range: backwards from the chapter after it, or forwards from the chapter
/// before it. Pages inside the range are saved on the visit that finds them,
/// so the only cost of walking is the pages between the neighbour and the
/// start of the stretch.
/// </remarks>
public static class RangePlanner
{
    public static RangePlan Plan(ScanResult scan, int from, int to, bool predict = true)
    {
        ArgumentNullException.ThrowIfNull(scan);

        if (!scan.Ready || scan.Layout is null)
        {
            return new RangePlan([], [], 0, scan.Problem ?? "Scan the novel first.");
        }

        if (from > to)
        {
            (from, to) = (to, from);
        }

        from = Math.Max(from, scan.FirstNumber);
        to = Math.Min(to, scan.LastNumber);
        if (from > to)
        {
            return new RangePlan([], [], 0, $"Choose chapters between {scan.FirstNumber} and {scan.LastNumber}.");
        }

        // A book numbered by place: its listed chapters are placed by the
        // site's own count, which can be a chapter out (a hidden or removed
        // entry). A range starting before them is counted from chapter 1
        // alone, so one book never mixes the two ways of numbering.
        if (scan.PositionalNumbers && scan.Layout.NextSelector is { } next
            && scan.Chapters.FirstOrDefault(c => c.Number == 1) is { } one)
        {
            var listedFrom = scan.Chapters.Where(c => c.Number > 1).Select(c => c.Number).DefaultIfEmpty(int.MaxValue).Min();
            if (from < listedFrom)
            {
                var walk = new WalkSpec(one.Url, 1, next, +1, from, to, to + 1);
                return new RangePlan([], [walk], from - 1, null);
            }
        }

        var known = scan.Chapters.ToDictionary(c => c.Number);
        var direct = known.Values.Where(c => c.Number >= from && c.Number <= to)
            .Select(c => new NumberedLink(c.Number, c.Url)).ToList();

        // Chapters no page lists, when their addresses can be built instead.
        // Never from a place in the book: addresses follow the site's numbers.
        List<NumberedLink> predicted = [];
        if (predict && !scan.PositionalNumbers && AddressPattern.Find(scan.Chapters) is { } pattern)
        {
            foreach (var (start, end) in Gaps(from, to, known.Keys))
            {
                for (var n = start; n <= end; n++)
                {
                    predicted.Add(new NumberedLink(n, pattern.For(n)));
                }
            }

            direct.AddRange(predicted);
            direct.Sort((x, y) => x.Number.CompareTo(y.Number));
            return new RangePlan(direct, [], 0, null, predicted);
        }

        List<WalkSpec> walks = [];
        var extra = 0;
        List<string> unreachable = [];
        foreach (var (start, end) in Gaps(from, to, known.Keys))
        {
            var below = scan.Chapters.LastOrDefault(c => c.Number < start);
            var above = scan.Chapters.FirstOrDefault(c => c.Number > end);

            // Pages visited outside the range to get there. A neighbour inside
            // the range costs nothing: the walk saves it on its way.
            var nextCost = below is not null && scan.Layout.NextSelector is not null
                ? Math.Max(0, from - below.Number)
                : int.MaxValue;
            var prevCost = above is not null && scan.Layout.PrevSelector is not null
                ? Math.Max(0, above.Number - to)
                : int.MaxValue;

            if (nextCost == int.MaxValue && prevCost == int.MaxValue)
            {
                unreachable.Add(start == end ? $"{start}" : $"{start}-{end}");
                continue;
            }

            // When the neighbour is inside the range the walk takes it over,
            // so it is loaded once, not once directly and again as a step.
            if (nextCost <= prevCost)
            {
                var saveFrom = below!.Number >= from ? below.Number : start;
                direct.RemoveAll(link => link.Number == below.Number);
                walks.Add(new WalkSpec(below.Url, below.Number, scan.Layout.NextSelector!, +1, saveFrom, end, end - below.Number + 2));
                extra += nextCost;
            }
            else
            {
                var saveTo = above!.Number <= to ? above.Number : end;
                direct.RemoveAll(link => link.Number == above.Number);
                walks.Add(new WalkSpec(above.Url, above.Number, scan.Layout.PrevSelector!, -1, start, saveTo, above.Number - start + 2));
                extra += prevCost;
            }
        }

        var problem = unreachable.Count == 0
            ? null
            : $"No page lists chapter(s) {string.Join(", ", unreachable)}, and the chapter pages have no next or previous link to reach them.";
        return new RangePlan(direct, walks, extra, problem);
    }

    /// <summary>Stretches of chapter numbers inside the range with no known address.</summary>
    internal static IEnumerable<(int Start, int End)> Gaps(int from, int to, IEnumerable<int> known)
    {
        var have = known.ToHashSet();
        int? start = null;
        for (var n = from; n <= to; n++)
        {
            if (!have.Contains(n))
            {
                start ??= n;
                continue;
            }

            if (start is { } s)
            {
                yield return (s, n - 1);
                start = null;
            }
        }

        if (start is { } last)
        {
            yield return (last, to);
        }
    }
}

/// <summary>
/// A book whose chapter addresses differ only by a number that moves with
/// the chapter number: /book/chapter-350, /book_350.html, or an id that is
/// always the chapter number plus the same offset.
/// </summary>
/// <remarks>
/// Only accepted when every known chapter agrees, and the known chapters
/// cover a real span (chapter 1 and the newest, say), so a coincidence
/// between two neighbours cannot pass for a pattern. A built address is
/// still checked before a run relies on it: the session opens one and reads
/// its title.
/// </remarks>
public sealed record AddressPattern(string Prefix, string Suffix, long Offset)
{
    public string For(int number) => this.Prefix + (number + this.Offset).ToString(System.Globalization.CultureInfo.InvariantCulture) + this.Suffix;

    public static AddressPattern? Find(IReadOnlyList<ScannedChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        if (chapters.Count < 2 || chapters[^1].Number - chapters[0].Number < 10)
        {
            return null;
        }

        AddressPattern? found = null;
        foreach (var chapter in chapters)
        {
            var split = Split(chapter.Url);
            if (split is null)
            {
                return null;
            }

            var candidate = new AddressPattern(split.Value.Prefix, split.Value.Suffix, split.Value.Id - chapter.Number);
            if (found is null)
            {
                found = candidate;
            }
            else if (found != candidate)
            {
                return null;
            }
        }

        return found;
    }

    /// <summary>The address around its last run of digits, before any extension.</summary>
    private static (string Prefix, long Id, string Suffix)? Split(string url)
    {
        var query = url.IndexOfAny(['?', '#']);
        var path = query >= 0 ? url[..query] : url;
        var end = path.Length;
        while (end > 0 && !char.IsDigit(path[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && char.IsDigit(path[start - 1]))
        {
            start--;
        }

        var tail = path[end..];
        if (end == start || end - start > 12 || tail.Length > 6 || tail.Any(char.IsLetterOrDigit) && tail is not ".html" and not ".htm" and not "/" and not ".html/")
        {
            return null;
        }

        return long.TryParse(path.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? (path[..start], id, url[end..])
            : null;
    }
}

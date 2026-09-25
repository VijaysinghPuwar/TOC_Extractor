namespace TocExtractor.Core.Links;

/// <summary>When two URLs name the same chapter.</summary>
/// <remarks>
/// <para>
/// One definition, used by every place that has to decide whether two links are
/// the same: deduplication while collecting, the resume check, and the
/// link-set comparison. Python has two — an exact string match for dedup and
/// the resume check, and a normalising one for comparing link sets — and they
/// disagree in both directions.
/// </para>
/// <para>
/// A table of contents offering both <c>/ch1</c> and <c>/ch1#top</c> has them
/// kept as two chapters and fetched twice, because dedup compares strings; the
/// same page is then written under two numbers. In the other direction, a site
/// that starts emitting a trailing slash makes the link-set comparison report
/// "identical" while the resume check finds nothing already done, so a run that
/// says it is resuming re-sends every request.
/// </para>
/// <para>
/// Trailing slash and fragment are cosmetic: a fragment is never sent to the
/// server, so two URLs differing only there are one request. Query parameters
/// are kept, because on plenty of tables of contents they carry the chapter
/// identity.
/// </para>
/// </remarks>
public static class UrlIdentity
{
    /// <summary>The canonical form of <paramref name="url"/>, for comparison only.</summary>
    /// <remarks>Never use the result as a URL to fetch; keep the original for that.</remarks>
    public static string Of(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return url;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/";
        }

        var builder = new UriBuilder(parsed)
        {
            Scheme = parsed.Scheme.ToLowerInvariant(),
            Host = parsed.Host.ToLowerInvariant(),
            Path = path,
            Fragment = string.Empty,
        };

        // UriBuilder writes the default port back out; drop it so
        // "https://e.com" and "https://e.com:443" agree.
        if (parsed.IsDefaultPort)
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }

    /// <summary>A comparer over <see cref="Of"/>, for dictionaries and sets.</summary>
    public static IEqualityComparer<string> Comparer { get; } = new IdentityComparer();

    public static bool SameChapter(string left, string right) =>
        string.Equals(Of(left), Of(right), StringComparison.Ordinal);

    private sealed class IdentityComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) =>
            x is null || y is null ? ReferenceEquals(x, y) : SameChapter(x, y);

        public int GetHashCode(string obj) => Of(obj).GetHashCode(StringComparison.Ordinal);
    }
}

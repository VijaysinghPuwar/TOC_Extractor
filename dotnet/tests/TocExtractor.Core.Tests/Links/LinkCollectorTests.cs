using TocExtractor.Core.Links;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Politeness;

namespace TocExtractor.Core.Tests.Links;

public sealed class LinkCollectorTests
{
    private const string Robots = "User-agent: *\nDisallow: /members/\n";

    private static readonly UrlGuard Guard = new(
        resolver: new FixedResolver(
            ("example.com", ["93.184.216.34"]),
            ("other.example", ["93.184.216.35"])));

    private static RobotsPolicy Policy() =>
        RobotsPolicy.Parse(Robots, "https://example.com");

    // -- the invariant -------------------------------------------------------

    [Fact]
    public void Every_candidate_is_kept_or_rejected()
    {
        object?[] raw =
        [
            "https://example.com/ch/1",
            "https://example.com/ch/2",
            new Dictionary<string, string>(),   // SVG anchor
            null,                               // element with no href
            "file:///etc/passwd",
            "javascript:alert(1)",
            "",
            "https://example.com/ch/1",         // duplicate
            "http://127.0.0.1/admin",
        ];

        var tally = LinkCollector.Collect(raw, Guard).Collection;

        Assert.Equal(raw.Length, tally.RawCount);
        Assert.Equal(raw.Length, tally.Kept.Count + tally.Rejected.Count);
        Assert.Equal(["https://example.com/ch/1", "https://example.com/ch/2"], tally.Kept);
    }

    /// <summary>Constructing an unbalanced tally must be impossible, not merely logged.</summary>
    [Fact]
    public void Accounting_failure_is_an_error_not_a_silent_drop()
    {
        var thrown = Assert.Throws<LinkAccountingException>(
            () => new LinkTally(rawCount: 5, kept: ["a"]));

        Assert.Contains("link accounting lost", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Truncation_is_accounted_separately_from_rejection()
    {
        object?[] raw = [.. Enumerable.Range(0, 10).Select(i => $"https://example.com/ch/{i}")];

        var tally = LinkCollector.Collect(raw, Guard, maxLinks: 3).Collection;

        Assert.Equal(3, tally.Kept.Count);
        Assert.Equal(7, tally.Truncated);
        Assert.Empty(tally.Rejected);
        Assert.Equal(tally.RawCount, tally.Kept.Count + tally.Truncated);
    }

    [Fact]
    public void Truncation_keeps_decisions_aligned_with_kept_links()
    {
        object?[] raw = [.. Enumerable.Range(0, 10).Select(i => $"https://example.com/ch/{i}")];

        var vetted = LinkCollector.Collect(raw, Guard, maxLinks: 3);

        Assert.Equal(vetted.Collection.Kept.Count, vetted.Decisions.Count);
    }

    [Fact]
    public void Reason_counts_are_manifest_ready()
    {
        object?[] raw =
        [
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            "file:///x",
            "",
            "https://example.com/ok",
        ];

        var counts = LinkCollector.Collect(raw, Guard).Collection.ReasonCounts();

        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["not_a_string"] = 2,
                ["disallowed_scheme"] = 1,
                ["empty"] = 1,
            },
            counts);
    }

    [Fact]
    public void Svg_anchor_is_rejected_with_a_reason()
    {
        var tally = LinkCollector.Collect([new Dictionary<string, string>()], Guard).Collection;

        Assert.Empty(tally.Kept);
        Assert.Equal(RejectionReason.NotAString, tally.Rejected[0].Reason);
    }

    [Fact]
    public void Duplicates_are_counted()
    {
        object?[] raw = ["https://example.com/a", "https://example.com/a"];

        var tally = LinkCollector.Collect(raw, Guard).Collection;

        Assert.Equal(["https://example.com/a"], tally.Kept);
        Assert.Equal(RejectionReason.Duplicate, tally.Rejected[0].Reason);
    }

    [Fact]
    public void Rejected_link_describes_itself() =>
        Assert.Equal(
            "file:///x: disallowed_scheme (file)",
            new RejectedLink("file:///x", RejectionReason.DisallowedScheme, "file").Describe());

    [Fact]
    public void Rejected_link_without_detail_omits_the_parenthesis() =>
        Assert.Equal(
            "https://x/a: duplicate",
            new RejectedLink("https://x/a", RejectionReason.Duplicate).Describe());

    // -- robots integration --------------------------------------------------

    [Fact]
    public void Robots_disallow_is_a_hard_rejection_when_anonymous()
    {
        object?[] raw = ["https://example.com/members/1", "https://example.com/public/1"];

        var vetted = LinkCollector.Collect(raw, Guard, Policy());

        Assert.Equal(["https://example.com/public/1"], vetted.Collection.Kept);
        Assert.Equal(RejectionReason.RobotsDisallowed, vetted.Collection.Rejected[0].Reason);
        Assert.Contains("line 2", vetted.Collection.Rejected[0].Detail, StringComparison.Ordinal);
        Assert.Single(vetted.Decisions);
    }

    /// <summary>
    /// The escape hatch is a human action, not a flag. A site routinely
    /// disallows the paths a login unlocks, so refusing hard here would fail the
    /// signed-in flow and teach people to disable the check.
    /// </summary>
    [Fact]
    public void Authenticated_session_overrides_and_records_the_rule()
    {
        var vetted = LinkCollector.Collect(
            ["https://example.com/members/1"], Guard, Policy(), sessionAuthenticated: true);

        Assert.Equal(["https://example.com/members/1"], vetted.Collection.Kept);
        Assert.Empty(vetted.Collection.Rejected);

        var decision = vetted.Decisions[0];
        Assert.True(decision.AuthenticatedOverride);
        Assert.NotNull(decision.RuleDescription);
        Assert.Contains("line 2", decision.RuleDescription, StringComparison.Ordinal);

        var entry = decision.AsManifestEntry();
        Assert.Equal(true, entry["robots_authenticated_override"]);
        Assert.NotNull(entry["robots_rule"]);
    }

    [Fact]
    public void No_robots_policy_permits_everything()
    {
        var vetted = LinkCollector.Collect(["https://example.com/x"], Guard, robots: null);

        Assert.Equal(["https://example.com/x"], vetted.Collection.Kept);
        Assert.False(vetted.Decisions[0].AuthenticatedOverride);
    }

    /// <summary>An override marker on a permitted URL would poison the audit trail.</summary>
    [Fact]
    public void Authenticated_override_is_not_recorded_when_robots_permits()
    {
        var vetted = LinkCollector.Collect(
            ["https://example.com/public/1"], Guard, Policy(), sessionAuthenticated: true);

        Assert.False(vetted.Decisions[0].AuthenticatedOverride);
        Assert.Null(vetted.Decisions[0].RuleDescription);
    }

    // -- selectors -----------------------------------------------------------

    [Fact]
    public void Selector_set_reports_what_is_missing()
    {
        var selectors = SelectorSet.Create("a.ch", "", "  ");

        Assert.False(selectors.Complete);
        Assert.Equal(["title", "content"], selectors.Missing);
    }

    [Fact]
    public void Complete_selector_set_is_trimmed()
    {
        var selectors = SelectorSet.Create("  a.ch  ", "h1", "article");

        Assert.True(selectors.Complete);
        Assert.Equal("a.ch", selectors.Link);
    }

    /// <summary>
    /// The collector script must read the href attribute and resolve it against
    /// the document base. Reading the property instead is what returned an
    /// SVGAnimatedString for SVG anchors and an already-absolute URL for
    /// ordinary ones. Asserted against a real DOM in the browser suite; here
    /// only the shape that would silently regress.
    /// </summary>
    [Fact]
    public void Collector_script_resolves_the_attribute_against_the_document_base()
    {
        Assert.Contains("getAttribute('href')", LinkCollector.Script, StringComparison.Ordinal);
        Assert.Contains("document.baseURI", LinkCollector.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("el.href", LinkCollector.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bug: dedup compared raw strings while resume compared normalised ones, so
    /// a table of contents linking both /a and /a#top fetched the same page
    /// twice and wrote it under two chapter numbers. A fragment is never sent to
    /// the server, so those are one request.
    /// </summary>
    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a#top")]
    [InlineData("https://example.com/a", "https://example.com/a/")]
    [InlineData("https://example.com/a/", "https://example.com/a")]
    [InlineData("https://example.com/a", "https://EXAMPLE.com/a")]
    public void Cosmetically_different_urls_are_one_chapter(string first, string second)
    {
        var tally = LinkCollector.Collect([first, second], Guard).Collection;

        Assert.Equal([first], tally.Kept);
        Assert.Equal(RejectionReason.Duplicate, tally.Rejected[0].Reason);
    }

    /// <summary>A query parameter does change the page, so it is not cosmetic.</summary>
    [Fact]
    public void Different_query_parameters_are_different_chapters()
    {
        var tally = LinkCollector
            .Collect(["https://example.com/r?ch=1", "https://example.com/r?ch=2"], Guard)
            .Collection;

        Assert.Equal(2, tally.Kept.Count);
    }
}

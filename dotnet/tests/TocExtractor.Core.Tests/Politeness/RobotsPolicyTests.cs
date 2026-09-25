using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

public sealed class RobotsPolicyTests
{
    private const string Robots = """
        # comment line
        User-agent: *
        Disallow: /private/
        Crawl-delay: 4

        User-agent: TOCExtractor
        Disallow: /members/
        Allow: /members/public/

        """;

    private static RobotsPolicy Parse(string userAgent = Core.Politeness.Robots.DefaultUserAgent) =>
        RobotsPolicy.Parse(Robots, "https://example.com", userAgent);

    [Fact]
    public void Allows_an_unlisted_path() =>
        Assert.True(Parse().CanFetch("https://example.com/chapter/1"));

    [Fact]
    public void Denies_a_listed_path() =>
        Assert.False(Parse().CanFetch("https://example.com/members/secret"));

    /// <summary>The post-gate warning has to name the rule and where it lives.</summary>
    [Fact]
    public void Matched_rule_reports_the_line_number()
    {
        var rule = Parse().MatchedRule("https://example.com/members/secret");

        Assert.NotNull(rule);
        Assert.Equal("/members/", rule.Value);
        Assert.Equal(7, rule.LineNumber);
        Assert.Contains("line 7", rule.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Matched_rule_prefers_the_most_specific_directive()
    {
        var policy = RobotsPolicy.Parse(
            "User-agent: *\nDisallow: /a/\nDisallow: /a/b/\n", "https://example.com");

        var rule = policy.MatchedRule("https://example.com/a/b/c");

        Assert.NotNull(rule);
        Assert.Equal("/a/b/", rule.Value);
        Assert.Equal(3, rule.LineNumber);
    }

    [Fact]
    public void Matched_rule_is_none_for_a_permitted_path() =>
        Assert.Null(Parse().MatchedRule("https://example.com/chapter/1"));

    [Fact]
    public void Crawl_delay_is_read_from_the_applicable_group() =>
        Assert.Equal(TimeSpan.FromSeconds(4), Parse("SomeOtherBot").CrawlDelay);

    /// <summary>
    /// robots.txt precedence is winner-takes-all. TOCExtractor has its own group
    /// with no Crawl-delay, so the wildcard group's does not reach it.
    /// </summary>
    [Fact]
    public void Crawl_delay_does_not_leak_from_the_wildcard_group() =>
        Assert.Null(Parse().CrawlDelay);

    [Fact]
    public void Wildcard_group_is_ignored_when_a_specific_one_exists()
    {
        var policy = Parse();

        Assert.True(policy.CanFetch("https://example.com/private/x"));
        Assert.Null(policy.MatchedRule("https://example.com/private/x"));
    }

    [Fact]
    public void Wildcard_group_applies_when_no_specific_group_matches()
    {
        var policy = Parse("SomeOtherBot");

        Assert.False(policy.CanFetch("https://example.com/private/x"));
        Assert.Equal(3, policy.MatchedRule("https://example.com/private/x")?.LineNumber);
    }

    /// <summary>RFC 9309: an unreachable robots.txt means no restrictions.</summary>
    [Fact]
    public void Missing_robots_permits_everything()
    {
        var policy = RobotsPolicy.Missing("https://example.com");

        Assert.True(policy.CanFetch("https://example.com/anything"));
        Assert.False(policy.Fetched);
        Assert.Null(policy.CrawlDelay);
    }

    [Fact]
    public void Comments_do_not_become_rules()
    {
        var policy = RobotsPolicy.Parse("# Disallow: /everything\nUser-agent: *\nAllow: /\n", "x");

        Assert.True(policy.CanFetch("https://example.com/everything"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# nothing here\n")]
    [InlineData("\n\n")]
    public void A_body_with_no_directives_permits_everything(string body) =>
        Assert.True(RobotsPolicy.Parse(body, "https://e.com").CanFetch("https://e.com/anything"));

    [Fact]
    public void Origin_of_strips_path_and_query() =>
        Assert.Equal(
            "https://example.com",
            Core.Politeness.Robots.OriginOf("https://example.com/book/toc?page=2#x"));

    // -- rule semantics, matched against urllib.robotparser ------------------

    /// <summary>An Allow beats a broader Disallow wherever it sits in the file.</summary>
    [Theory]
    [InlineData("User-agent: *\nDisallow: /members/\nAllow: /members/public/\n")]
    [InlineData("User-agent: *\nAllow: /members/public/\nDisallow: /members/\n")]
    public void Longer_allow_overrides_a_broader_disallow(string content)
    {
        var policy = RobotsPolicy.Parse(content, "https://e.com");

        Assert.True(policy.CanFetch("https://e.com/members/public/x"));
        Assert.False(policy.CanFetch("https://e.com/members/secret"));
    }

    [Fact]
    public void Star_matches_inside_a_path()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /*/secret\n", "https://e.com");

        Assert.False(policy.CanFetch("https://e.com/a/secret"));
        Assert.False(policy.CanFetch("https://e.com/b/secret"));
        Assert.True(policy.CanFetch("https://e.com/a/public"));
    }

    [Fact]
    public void Dollar_anchors_the_end_of_the_path()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /*.pdf$\n", "https://e.com");

        Assert.False(policy.CanFetch("https://e.com/doc.pdf"));
        Assert.True(policy.CanFetch("https://e.com/doc.pdf.html"));
    }

    /// <summary>An empty Disallow means allow everything, not disallow everything.</summary>
    [Fact]
    public void Empty_disallow_permits_everything() =>
        Assert.True(
            RobotsPolicy.Parse("User-agent: *\nDisallow:\n", "https://e.com")
                .CanFetch("https://e.com/anything"));

    [Fact]
    public void Disallow_slash_refuses_everything()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /\n", "https://e.com");

        Assert.False(policy.CanFetch("https://e.com/anything"));
        Assert.NotNull(policy.MatchedRule("https://e.com/anything"));
    }

    // -- the decision and the rule can never disagree ------------------------

    /// <summary>
    /// Python answers these two questions with two different algorithms:
    /// <c>can_fetch</c> selects the group by substring, <c>matched_rule</c> by
    /// prefix. For "MyTOCExtractorBot" they pick different groups, so the
    /// permitted path is reported with a Disallow that never applied. One
    /// evaluation answers both here, and this pins that.
    /// </summary>
    [Theory]
    [InlineData("TOCExtractor")]
    [InlineData("TOCExtractor/2.0")]
    [InlineData("MyTOCExtractorBot")]
    [InlineData("tocextractor")]
    [InlineData("SomeOtherBot")]
    [InlineData("*")]
    public void Decision_and_matched_rule_never_disagree(string userAgent)
    {
        var policy = RobotsPolicy.Parse(Robots, "https://e.com", userAgent);

        foreach (var path in new[] { "/private/x", "/members/secret", "/members/public/x", "/ch/1" })
        {
            var url = "https://e.com" + path;

            Assert.Equal(policy.CanFetch(url), policy.MatchedRule(url) is null);
        }
    }

    /// <summary>
    /// A token that merely contains a group's name is not that agent. Matching
    /// is on the product token, per RFC 9309.
    /// </summary>
    [Fact]
    public void An_agent_containing_a_group_name_does_not_join_that_group()
    {
        var policy = RobotsPolicy.Parse(Robots, "https://e.com", "MyTOCExtractorBot");

        Assert.False(policy.CanFetch("https://e.com/private/x"));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.CrawlDelay);
    }

    [Fact]
    public void A_versioned_product_token_still_matches_its_group()
    {
        var policy = RobotsPolicy.Parse(Robots, "https://e.com", "TOCExtractor/2.0");

        Assert.True(policy.CanFetch("https://e.com/private/x"));
        Assert.Null(policy.CrawlDelay);
    }
}

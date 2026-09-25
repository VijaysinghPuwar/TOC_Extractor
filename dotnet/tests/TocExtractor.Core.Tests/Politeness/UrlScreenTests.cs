using System.Net;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

/// <summary>A resolver that counts how often it is asked.</summary>
internal sealed class CountingResolver(IHostResolver inner) : IHostResolver
{
    internal List<string> Asked { get; } = [];

    public IReadOnlyList<IPAddress> Resolve(string host)
    {
        this.Asked.Add(host);
        return inner.Resolve(host);
    }
}

public sealed class UrlScreenTests
{
    private static UrlGuard Guard(IHostResolver resolver) => new(resolver: resolver);

    /// <summary>
    /// Bug: the cache stores whether a URL passed, so every sighting after the
    /// first returns no reason. Callers fall back to "malformed", and a loopback
    /// address is reported to the user as a syntax error.
    /// </summary>
    [Fact]
    public void Repeated_check_keeps_the_reason_and_the_detail()
    {
        var screen = new UrlScreen(Guard(FixedResolver.Public));
        const string Url = "http://127.0.0.1/admin";

        var first = screen.Check(Url);
        var second = screen.Check(Url);

        Assert.Equal(RejectionReason.PrivateAddress, first.Reason);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Detail, second.Detail);
        Assert.NotEmpty(second.Detail);
    }

    [Fact]
    public void Repeated_check_consults_the_guard_once()
    {
        var screen = new UrlScreen(Guard(FixedResolver.Public));

        for (var i = 0; i < 10; i++)
        {
            screen.Check("https://example.com/a");
        }

        Assert.Equal(1, screen.GuardCalls);
    }

    [Fact]
    public void Distinct_urls_are_each_screened()
    {
        var screen = new UrlScreen(Guard(FixedResolver.Public));

        screen.Check("https://example.com/a");
        screen.Check("https://example.com/b");

        Assert.Equal(2, screen.GuardCalls);
    }

    /// <summary>
    /// A page of forty images on one host is forty screened requests. Python
    /// keys its cache on the URL, so it never reuses the resolution its own
    /// comment says the cache exists to avoid — measured at forty lookups.
    /// </summary>
    [Fact]
    public void Forty_assets_on_one_host_resolve_once()
    {
        var counting = new CountingResolver(FixedResolver.Public);
        var screen = new UrlScreen(Guard(new CachingHostResolver(counting)));

        for (var i = 0; i < 40; i++)
        {
            screen.Check($"https://cdn.example.com/img/{i}.png");
        }

        Assert.Equal(40, screen.GuardCalls);
        Assert.Equal(["cdn.example.com"], counting.Asked);
    }

    [Fact]
    public void Separate_hosts_are_each_resolved()
    {
        var counting = new CountingResolver(FixedResolver.Public);
        var resolver = new CachingHostResolver(counting);

        var screen = new UrlScreen(Guard(resolver));
        screen.Check("https://example.com/a");
        screen.Check("https://cdn.example.com/b");

        Assert.Equal(2, resolver.Lookups);
    }

    /// <summary>
    /// A host that does not resolve will not resolve on the next image either.
    /// Retrying it once per asset is the same waste wearing a different hat.
    /// </summary>
    [Fact]
    public void A_failing_host_is_only_attempted_once()
    {
        var counting = new CountingResolver(FixedResolver.Public);
        var resolver = new CachingHostResolver(counting);
        var screen = new UrlScreen(Guard(resolver));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(
                RejectionReason.UnresolvableHost,
                screen.Check($"https://nope.invalid/{i}").Reason);
        }

        Assert.Equal(1, resolver.Lookups);
    }

    [Fact]
    public void A_literal_address_needs_no_resolver_at_all()
    {
        var counting = new CountingResolver(FixedResolver.Public);
        var screen = new UrlScreen(Guard(new CachingHostResolver(counting)));

        screen.Check("https://93.184.216.34/a");

        Assert.Empty(counting.Asked);
    }
}

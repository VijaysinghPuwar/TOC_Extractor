using System.Net;
using System.Net.Sockets;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests.Politeness;

/// <summary>A resolver backed by a table, so no test touches DNS.</summary>
internal sealed class FixedResolver(params (string Host, string[] Addresses)[] entries) : IHostResolver
{
    private readonly Dictionary<string, string[]> table =
        entries.ToDictionary(entry => entry.Host, entry => entry.Addresses, StringComparer.OrdinalIgnoreCase);

    internal static FixedResolver Public { get; } = new(
        ("example.com", ["93.184.216.34"]),
        ("cdn.example.com", ["93.184.216.35"]));

    public IReadOnlyList<IPAddress> Resolve(string host) =>
        this.table.TryGetValue(host, out var addresses)
            ? [.. addresses.Select(IPAddress.Parse)]
            : throw new SocketException((int)SocketError.HostNotFound);
}

public sealed class UrlGuardTests
{
    /// <summary>
    /// The v1 regression: every one of these went straight to the browser,
    /// because joining a base URL against a value that carries its own scheme
    /// returns it untouched. A file: URL was read off disk and written into the
    /// output directory as chapter text.
    /// </summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("file:///Users/someone/.ssh/id_ed25519")]
    [InlineData("javascript:alert(document.cookie)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("ftp://example.com/x")]
    [InlineData("about:blank")]
    [InlineData("chrome://settings")]
    public void Rejects_non_http_schemes(string url)
    {
        var verdict = new UrlGuard(resolver: FixedResolver.Public).Check(url);

        Assert.False(verdict.Allowed);
        Assert.Equal(RejectionReason.DisallowedScheme, verdict.Reason);
    }

    [Theory]
    [InlineData("https://example.com/ch/1")]
    [InlineData("http://example.com/ch/2")]
    public void Allows_http_and_https(string url) =>
        Assert.True(new UrlGuard(resolver: FixedResolver.Public).Check(url).Allowed);

    [Fact]
    public void Scheme_check_is_case_insensitive()
    {
        var guard = new UrlGuard(resolver: FixedResolver.Public);

        Assert.True(guard.Check("HTTPS://example.com/x").Allowed);
        Assert.False(guard.Check("FILE:///etc/passwd").Allowed);
    }

    /// <summary>
    /// The SVG-anchor bug. An SVG anchor's href is an SVGAnimatedString, truthy
    /// in JavaScript and falsy once it crosses into the host language, so v1's
    /// truthiness filter discarded it with no error and the chapter vanished.
    /// Here it is a counted rejection.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonStrings))]
    public void Counts_non_string_candidates_rather_than_dropping_them(object? value, string typeName)
    {
        var verdict = new UrlGuard(resolver: FixedResolver.Public).Check(value);

        Assert.False(verdict.Allowed);
        Assert.Equal(RejectionReason.NotAString, verdict.Reason);
        Assert.Contains(typeName, verdict.Detail, StringComparison.Ordinal);
    }

    public static TheoryData<object?, string> NonStrings() => new()
    {
        { null, "null" },
        { 42, "Int32" },
        { new Dictionary<string, string>(), "Dictionary`2" },
        { Array.Empty<string>(), "String[]" },
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Counts_empty_candidates(string value) =>
        Assert.Equal(
            RejectionReason.Empty,
            new UrlGuard(resolver: FixedResolver.Public).Check(value).Reason);

    /// <summary>Resolution happens before the guard; anything still relative is a caller bug.</summary>
    [Fact]
    public void Rejects_a_relative_url_as_schemeless() =>
        Assert.Equal(
            RejectionReason.DisallowedScheme,
            new UrlGuard(resolver: FixedResolver.Public).Check("httpd-docs/ch1").Reason);

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin", "loopback")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "cloud metadata endpoint")]
    [InlineData("http://10.0.0.5/internal", "RFC1918 class A")]
    [InlineData("http://192.168.1.1/router", "RFC1918 class C")]
    [InlineData("http://172.16.0.9/x", "RFC1918 class B")]
    [InlineData("http://[::1]/x", "IPv6 loopback")]
    [InlineData("http://[fe80::1]/x", "IPv6 link-local")]
    [InlineData("http://[fd00::1]/x", "IPv6 unique-local")]
    [InlineData("http://0.0.0.0/x", "unspecified")]
    [InlineData("http://100.64.0.1/x", "carrier-grade NAT")]
    public void Rejects_private_addresses(string url, string label)
    {
        var guard = new UrlGuard(resolver: new FixedResolver(("localhost", ["127.0.0.1"])));

        var verdict = guard.Check(url);

        Assert.False(verdict.Allowed, label);
        Assert.Equal(RejectionReason.PrivateAddress, verdict.Reason);
    }

    [Fact]
    public void Rejects_loopback_reached_by_name()
    {
        var guard = new UrlGuard(resolver: new FixedResolver(("localhost", ["127.0.0.1"])));

        Assert.Equal(RejectionReason.PrivateAddress, guard.Check("http://localhost:3000/x").Reason);
    }

    /// <summary>::ffff:127.0.0.1 is loopback wearing an IPv6 hat.</summary>
    [Fact]
    public void Rejects_ipv4_mapped_ipv6_loopback() =>
        Assert.Equal(
            RejectionReason.PrivateAddress,
            new UrlGuard(resolver: FixedResolver.Public).Check("http://[::ffff:127.0.0.1]/x").Reason);

    /// <summary>A public name pointed at loopback is the usual SSRF shape.</summary>
    [Fact]
    public void Rejects_a_public_name_resolving_to_a_private_address()
    {
        var guard = new UrlGuard(resolver: new FixedResolver(("evil.example", ["127.0.0.1"])));

        var verdict = guard.Check("https://evil.example/x");

        Assert.Equal(RejectionReason.PrivateAddress, verdict.Reason);
        Assert.Contains("evil.example -> 127.0.0.1", verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void One_private_answer_rejects_even_when_others_are_public()
    {
        var guard = new UrlGuard(
            resolver: new FixedResolver(("mixed.example", ["93.184.216.34", "10.0.0.1"])));

        Assert.False(guard.Check("https://mixed.example/x").Allowed);
    }

    [Fact]
    public void Rejects_an_unresolvable_host() =>
        Assert.Equal(
            RejectionReason.UnresolvableHost,
            new UrlGuard(resolver: FixedResolver.Public).Check("https://nope.invalid/x").Reason);

    [Fact]
    public void Allow_private_hosts_opens_the_gate() =>
        Assert.True(
            new UrlGuard(allowPrivateHosts: true, resolver: FixedResolver.Public)
                .Check("http://127.0.0.1:8080/admin").Allowed);

    /// <summary>The private-host escape hatch must not also permit file: URLs.</summary>
    [Fact]
    public void Allow_private_hosts_does_not_open_the_scheme_gate()
    {
        var guard = new UrlGuard(allowPrivateHosts: true, resolver: FixedResolver.Public);

        var verdict = guard.Check("file:///etc/passwd");

        Assert.False(verdict.Allowed);
        Assert.Equal(RejectionReason.DisallowedScheme, verdict.Reason);
    }

    [Fact]
    public void Does_not_resolve_a_host_when_private_hosts_are_allowed()
    {
        // The resolver knows nothing, so a lookup would throw. Reaching a
        // verdict proves the gate short-circuits before DNS.
        var guard = new UrlGuard(allowPrivateHosts: true, resolver: new FixedResolver());

        Assert.True(guard.Check("http://anything.internal/x").Allowed);
    }
}

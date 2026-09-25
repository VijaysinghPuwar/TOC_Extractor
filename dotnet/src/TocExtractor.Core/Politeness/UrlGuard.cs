using System.Net.Sockets;

namespace TocExtractor.Core.Politeness;

/// <summary>Scheme allowlist plus private-address rejection.</summary>
/// <remarks>
/// This is the string-level check. The browser-backed page source enforces the
/// same policy again at the request layer, because a permitted host can redirect
/// to one that was never pre-flighted and no amount of care here sees that hop.
/// </remarks>
public sealed class UrlGuard(bool allowPrivateHosts = false, IHostResolver? resolver = null)
{
    private static readonly string[] AllowedSchemes = ["http", "https"];

    private readonly IHostResolver resolver = resolver ?? SystemHostResolver.Instance;

    /// <summary>Vet one value the DOM produced.</summary>
    /// <param name="candidate">
    /// Deliberately <see cref="object"/>. An SVG anchor's href is an
    /// SVGAnimatedString rather than a string, and a guard that only accepted
    /// strings would push the decision back to a caller that would drop it
    /// silently — which is how those chapters went missing in the first place.
    /// </param>
    public UrlVerdict Check(object? candidate)
    {
        if (candidate is not string text)
        {
            var described = candidate?.GetType().Name ?? "null";
            return UrlVerdict.Reject(
                Describe(candidate), RejectionReason.NotAString, $"got {described}");
        }

        var raw = text.Trim();
        if (raw.Length == 0)
        {
            return UrlVerdict.Reject(raw, RejectionReason.Empty);
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
        {
            // A value with no scheme is relative. Resolution happens before the
            // guard sees it, so anything still relative here is a caller bug,
            // and reporting it as a scheme problem is what says so.
            return UrlVerdict.Reject(
                raw,
                RejectionReason.DisallowedScheme,
                SchemeOf(raw));
        }

        if (!AllowedSchemes.Contains(url.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            // Why an allowlist rather than a better absolute-URL test: joining a
            // base against "file:///etc/passwd" returns the file URL untouched,
            // so no amount of care in the join step catches it.
            return UrlVerdict.Reject(raw, RejectionReason.DisallowedScheme, url.Scheme);
        }

        var host = url.Host;
        if (host.Length == 0)
        {
            return UrlVerdict.Reject(raw, RejectionReason.MissingHost);
        }

        return allowPrivateHosts ? UrlVerdict.Allow(raw) : CheckAddress(raw, url);
    }

    private UrlVerdict CheckAddress(string raw, Uri url)
    {
        var host = url.Host;

        // A literal needs no DNS round trip. Uri keeps IPv6 literals in
        // brackets, which is not what the parser accepts, so strip them.
        var literal = url.HostNameType is UriHostNameType.IPv6
            ? host.Trim('[', ']')
            : host;

        if (System.Net.IPAddress.TryParse(literal, out var parsed))
        {
            return PrivateAddress.IsPrivate(parsed)
                ? UrlVerdict.Reject(raw, RejectionReason.PrivateAddress, literal)
                : UrlVerdict.Allow(raw);
        }

        IReadOnlyList<System.Net.IPAddress> addresses;
        try
        {
            addresses = this.resolver.Resolve(host);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return UrlVerdict.Reject(
                raw, RejectionReason.UnresolvableHost, $"{host}: {exception.Message}");
        }

        if (addresses.Count == 0)
        {
            return UrlVerdict.Reject(raw, RejectionReason.UnresolvableHost, host);
        }

        // Every answer must be public. One private answer is enough to refuse,
        // because which address the browser picks is not ours to choose.
        foreach (var address in addresses)
        {
            if (PrivateAddress.IsPrivate(address))
            {
                return UrlVerdict.Reject(
                    raw, RejectionReason.PrivateAddress, $"{host} -> {address}");
            }
        }

        return UrlVerdict.Allow(raw);
    }

    private static string SchemeOf(string raw)
    {
        var colon = raw.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? raw[..colon] : "(none)";
    }

    private static string Describe(object? candidate) =>
        candidate switch
        {
            null => "None",
            string text => text,
            _ => candidate.ToString() ?? candidate.GetType().Name,
        };
}

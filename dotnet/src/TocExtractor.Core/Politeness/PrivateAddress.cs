using System.Net;
using System.Net.Sockets;

namespace TocExtractor.Core.Politeness;

/// <summary>Anything that is not a routable public address.</summary>
/// <remarks>
/// Python gets this from <c>ipaddress</c>; .NET has only
/// <see cref="IPAddress.IsLoopback"/>, so the ranges are spelled out. They
/// follow what <c>ipaddress</c> calls private, loopback, link-local, reserved,
/// multicast or unspecified, which together are what a URL guard has to refuse.
/// The one that earns its keep on its own is 169.254.0.0/16: it carries the
/// cloud instance-metadata endpoint, and a scraper that will fetch a URL off a
/// page is one redirect away from reading credentials out of it.
/// </remarks>
internal static class PrivateAddress
{
    internal static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // ::ffff:127.0.0.1 is loopback wearing an IPv6 hat. Unwrap before
        // classifying, or every v4 range below is bypassed by mapping it.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateV4(address),
            AddressFamily.InterNetworkV6 => IsPrivateV6(address),
            _ => true,
        };
    }

    /// <summary>True when <paramref name="text"/> parses as an IP and is not public.</summary>
    /// <remarks>A value that is not an IP at all is not private; it is a name to resolve.</remarks>
    internal static bool IsPrivateLiteral(string text) =>
        IPAddress.TryParse(text, out var address) && IsPrivate(address);

    private static bool IsPrivateV4(IPAddress address)
    {
        Span<byte> octets = stackalloc byte[4];
        address.TryWriteBytes(octets, out _);

        return octets[0] switch
        {
            0 => true,                                          // "this network", incl. 0.0.0.0
            10 => true,                                         // RFC 1918
            127 => true,                                        // loopback
            100 => octets[1] >= 64 && octets[1] <= 127,          // CGNAT 100.64/10
            169 => octets[1] == 254,                            // link-local, incl. metadata
            172 => octets[1] >= 16 && octets[1] <= 31,           // RFC 1918
            192 => octets[1] switch
            {
                0 => octets[2] is 0 or 2,                       // IETF protocol, TEST-NET-1
                88 => octets[2] == 99,                          // 6to4 relay anycast
                168 => true,                                    // RFC 1918
                _ => false,
            },
            198 => (octets[1] is 18 or 19) || (octets[1] == 51 && octets[2] == 100),
            203 => octets[1] == 0 && octets[2] == 113,          // TEST-NET-3
            >= 224 => true,                                     // multicast and reserved 240/4
            _ => false,
        };
    }

    private static bool IsPrivateV6(IPAddress address)
    {
        if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
        {
            return true;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);

        // fc00::/7 unique-local.
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return true;
        }

        // 2001:db8::/32 documentation.
        return bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8;
    }
}

using System.Net;
using System.Net.Sockets;

namespace TocExtractor.Core.Politeness;

/// <summary>Resolves a host name to addresses.</summary>
/// <remarks>
/// An interface so no test touches DNS. The guard has to know every address a
/// name answers with, not just the first, which is why this returns them all.
/// </remarks>
public interface IHostResolver
{
    /// <summary>Every address <paramref name="host"/> resolves to.</summary>
    /// <exception cref="SocketException">The host does not resolve.</exception>
    IReadOnlyList<IPAddress> Resolve(string host);
}

/// <summary>The resolver used outside tests.</summary>
public sealed class SystemHostResolver : IHostResolver
{
    public static SystemHostResolver Instance { get; } = new();

    public IReadOnlyList<IPAddress> Resolve(string host) => Dns.GetHostAddresses(host);
}

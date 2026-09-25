namespace TocExtractor.App;

/// <summary>Reads robots.txt over HTTP.</summary>
/// <remarks>
/// Plain HTTP rather than the browser, because this runs before any page is
/// loaded and needs no session.
/// </remarks>
public static class RobotsHttp
{
    private const int MaxBytes = 512_000;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The body, or null when there is nothing to read.</summary>
    /// <remarks>
    /// RFC 9309 says a 4xx means no restrictions, so a missing robots.txt
    /// permits everything. Failing to <em>reach</em> the server is a different
    /// thing and is reported: silently indistinguishable from "no robots.txt",
    /// a local TLS misconfiguration quietly turns every site into an
    /// unrestricted one. Failing open on somebody else's rules because of a
    /// problem on this machine is not a default to keep quiet about.
    /// </remarks>
    public static string? Fetch(string robotsUrl, string userAgent) =>
        Fetch(robotsUrl, userAgent, Console.Error.WriteLine);

    /// <summary>As <see cref="Fetch(string, string)"/>, reporting problems to <paramref name="warn"/>.</summary>
    public static string? Fetch(string robotsUrl, string userAgent, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, robotsUrl);
            request.Headers.UserAgent.ParseAdd(userAgent);

            using var response = Client.Send(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                warn(
                    $"could not read {robotsUrl} (HTTP {(int)response.StatusCode}). Proceeding as "
                    + "if it permits everything, which may not be what the site intends.");
                return null;
            }

            using var stream = response.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            var buffer = new char[MaxBytes];
            var read = reader.ReadBlock(buffer, 0, MaxBytes);
            return new string(buffer, 0, read);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            warn(
                $"could not reach {robotsUrl} ({exception.Message}). Proceeding as if it permits "
                + "everything. If this is a certificate error it is a problem on this machine, "
                + "not the site.");
            return null;
        }
    }
}

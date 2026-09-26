using System.Net;
using System.Text;

namespace TocExtractor.Browser.Tests;

/// <summary>A throwaway HTTP server for the tests that need a real browser.</summary>
/// <remarks>
/// Loopback, so these tests need no network and no fixture host. The URL guard
/// refuses loopback by default, which is exactly what it is for, so the tests
/// that use this pass the one exemption the guard understands.
/// </remarks>
internal sealed class LocalSite : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, Response> routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();

    internal LocalSite()
    {
        this.Port = FreePort();
        this.listener.Prefixes.Add($"http://127.0.0.1:{this.Port}/");

        // A second name for the same server, for tests that need two sites.
        this.listener.Prefixes.Add($"http://localhost:{this.Port}/");
        this.listener.Start();
        _ = Task.Run(this.ServeAsync);
    }

    internal int Port { get; }

    internal string Origin => $"http://127.0.0.1:{this.Port}";

    internal List<string> Requested { get; } = [];

    internal LocalSite Html(string path, string body)
    {
        this.routes[path] = new Response(200, "text/html; charset=utf-8", body, null);
        return this;
    }

    internal LocalSite Redirect(string path, string location)
    {
        this.routes[path] = new Response(302, "text/plain", "", location);
        return this;
    }

    internal string Url(string path) => this.Origin + path;

    public void Dispose()
    {
        this.stopping.Cancel();
        this.listener.Close();
        this.stopping.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!this.stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await this.listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            lock (this.Requested)
            {
                this.Requested.Add(path);
            }

            var response = this.routes.TryGetValue(path, out var found)
                ? found
                : new Response(404, "text/plain", "not found", null);

            context.Response.StatusCode = response.Status;
            context.Response.ContentType = response.ContentType;
            if (response.Location is not null)
            {
                context.Response.Headers["Location"] = response.Location;
            }

            var bytes = Encoding.UTF8.GetBytes(response.Body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private sealed record Response(int Status, string ContentType, string Body, string? Location);
}

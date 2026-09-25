using TocExtractor.Core.Links;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Browser.Tests;

/// <summary>
/// The behaviours only a real browser can settle. Everything else about the
/// fetch pipeline is decided above this layer and tested against a stub, so
/// only what genuinely needs Chromium is here.
/// </summary>
[Trait("Category", "Browser")]
public sealed class BrowserPageSourceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Loopback is what the guard exists to refuse, so the fixture server needs
    /// the one exemption a user could also pass.
    /// </summary>
    private static UrlGuard Guard => new(allowPrivateHosts: true);

    private static Task<BrowserPageSource> StartAsync(int pages = 1) =>
        BrowserPageSource.StartAsync(
            Guard,
            new BrowserPageSourceOptions
            {
                MaxPages = pages,
                OperationBudget = TimeSpan.FromSeconds(20),
            });

    /// <summary>
    /// The v1 defect, settled against a real DOM rather than by reasoning about
    /// the spec. Reading the href property yields an already-absolute URL for
    /// ordinary anchors and an SVGAnimatedString for SVG ones — which crosses
    /// into the host language as a non-string and was silently dropped.
    /// Resolving the attribute against the document base returns a plain string
    /// for every element kind.
    /// </summary>
    [Fact]
    public async Task Collector_script_returns_strings_for_every_element_kind()
    {
        using var site = new LocalSite();
        site.Html("/book/toc", """
            <!doctype html><html><body>
            <a class="c" href="/rel/ch1">anchor relative</a>
            <a class="c" href="chapter/ch2">anchor relative no slash</a>
            <a class="c" href="httpd-docs/ch3">anchor http-prefixed relative</a>
            <div class="c" href="/div/ch4">div with href</div>
            <my-link class="c" href="httpd-docs/ch5">custom element</my-link>
            <svg xmlns="http://www.w3.org/2000/svg"><a class="c" href="/svg/ch6"><text>s</text></a></svg>
            </body></html>
            """);

        await using var source = await StartAsync();
        var toc = await source.LoadTocAsync(site.Url("/book/toc"), ".c", cancellationToken: Token);

        Assert.All(toc.RawLinks, link => Assert.IsType<string>(link));
        Assert.Equal(
            [
                site.Url("/rel/ch1"),
                site.Url("/book/chapter/ch2"),
                site.Url("/book/httpd-docs/ch3"),
                site.Url("/div/ch4"),
                site.Url("/book/httpd-docs/ch5"),
                site.Url("/svg/ch6"),
            ],
            toc.RawLinks.Select(link => link!.ToString()));

        var tally = LinkCollector.Collect(toc.RawLinks, Guard).Collection;
        Assert.Equal(6, tally.Kept.Count);
        Assert.Empty(tally.Rejected);
    }

    [Fact]
    public async Task Chapter_fields_are_read_from_the_page()
    {
        using var site = new LocalSite();
        site.Html("/ch/1", "<h1 class='t'>Chapter One</h1><article class='b'>Body text.</article>");

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/1"), ".t", ".b", Token);

        Assert.Equal("Chapter One", chapter.Title);
        Assert.Equal("Body text.", chapter.Body);
    }

    /// <summary>
    /// The reason the redirect loop lives in the route handler: a handler fires
    /// once per navigation, and the browser follows the chain internally, so the
    /// final URL has to be tracked as the hops are validated.
    /// </summary>
    [Fact]
    public async Task Final_url_after_a_redirect_chain_is_reported()
    {
        using var site = new LocalSite();
        site.Redirect("/ch/1", "/ch/1-moved");
        site.Redirect("/ch/1-moved", "/ch/1-final");
        site.Html("/ch/1-final", "<h1 class='t'>Arrived</h1><article class='b'>Body.</article>");

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/1"), ".t", ".b", Token);

        Assert.Equal(site.Url("/ch/1-final"), chapter.FinalUrl);
        Assert.Equal("Arrived", chapter.Title);
    }

    /// <summary>
    /// A selector that matches nothing on a healthy page is not a timeout: the
    /// fetch loop is told never to retry it, so the distinction has to be real.
    /// </summary>
    [Fact]
    public async Task Missing_selector_is_reported_as_not_found()
    {
        using var site = new LocalSite();
        site.Html("/ch/1", "<h1 class='t'>Only a title</h1>");

        await using var source = await StartAsync(pages: 1);
        var options = new BrowserPageSourceOptions { OperationBudget = TimeSpan.FromSeconds(2) };
        await using var quick = await BrowserPageSource.StartAsync(Guard, options, Token);

        await Assert.ThrowsAsync<SelectorNotFoundException>(
            () => quick.LoadChapterAsync(site.Url("/ch/1"), ".t", ".missing", Token));
    }

    [Fact]
    public async Task Html_capture_returns_the_page_source()
    {
        using var site = new LocalSite();
        site.Html("/book/toc", "<!doctype html><html><body><a class='c' href='/a'>a</a></body></html>");

        await using var source = await StartAsync();
        var toc = await source.LoadTocAsync(
            site.Url("/book/toc"), ".c", captureHtml: true, cancellationToken: Token);

        Assert.NotNull(toc.Html);
        Assert.Contains("<a class=\"c\"", toc.Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page carrying a subresource the guard refuses must not have that
    /// request leave the machine. Screened and aborted, never proxied.
    /// </summary>
    [Fact]
    public async Task Disallowed_subresource_is_aborted_rather_than_fetched()
    {
        using var site = new LocalSite();
        site.Html("/ch/1", """
            <h1 class='t'>One</h1><article class='b'>Body.</article>
            <img src="file:///etc/passwd">
            """);

        // A guard with no exemption would also refuse the fixture host, so this
        // asserts the file: URL never became a request rather than the abort.
        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/1"), ".t", ".b", Token);

        Assert.Equal("One", chapter.Title);
        Assert.DoesNotContain("/etc/passwd", site.Requested, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Blocked_navigation_carries_the_reason_it_was_refused()
    {
        // No exemption here: loopback is refused, which is the point.
        await using var source = await BrowserPageSource.StartAsync(
            new UrlGuard(),
            new BrowserPageSourceOptions { OperationBudget = TimeSpan.FromSeconds(5) },
            Token);

        var thrown = await Assert.ThrowsAsync<PageBlockedException>(
            () => source.LoadChapterAsync("http://127.0.0.1:9/x", ".t", ".b", Token));

        Assert.Equal(RejectionReason.PrivateAddress, thrown.Reason);
    }

    /// <summary>
    /// Bug: the screening cache stored only whether a URL passed, so every
    /// sighting after the first lost the reason and reported a private address
    /// as a malformed URL. Asserted here through the real path that hits the
    /// cache twice.
    /// </summary>
    [Fact]
    public async Task A_repeated_blocked_navigation_keeps_its_reason()
    {
        await using var source = await BrowserPageSource.StartAsync(
            new UrlGuard(),
            new BrowserPageSourceOptions { OperationBudget = TimeSpan.FromSeconds(5) },
            Token);

        var first = await Assert.ThrowsAsync<PageBlockedException>(
            () => source.LoadChapterAsync("http://10.0.0.5/x", ".t", ".b", Token));
        var second = await Assert.ThrowsAsync<PageBlockedException>(
            () => source.LoadChapterAsync("http://10.0.0.5/x", ".t", ".b", Token));

        Assert.Equal(RejectionReason.PrivateAddress, first.Reason);
        Assert.Equal(RejectionReason.PrivateAddress, second.Reason);
        Assert.Equal(first.Detail, second.Detail);
    }

    [Fact]
    public async Task Concurrent_chapters_use_separate_pages()
    {
        using var site = new LocalSite();
        foreach (var i in Enumerable.Range(1, 4))
        {
            site.Html($"/ch/{i}", $"<h1 class='t'>Chapter {i}</h1><article class='b'>Body {i}.</article>");
        }

        await using var source = await StartAsync(pages: 3);

        var chapters = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            source.LoadChapterAsync(site.Url($"/ch/{i}"), ".t", ".b", Token)));

        Assert.Equal(
            ["Chapter 1", "Chapter 2", "Chapter 3", "Chapter 4"],
            chapters.Select(c => c.Title).Order());
    }
}

using System.Text.Json;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Browser.Tests;

/// <summary>
/// The scanner's page scripts, on pages shaped like the real sites that
/// broke them.
/// </summary>
[Trait("Category", "Browser")]
public sealed class ScannerScriptTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string FindChapters => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "find-chapters.js"));

    private static string FindContent => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Scripts", "find-content.js"));

    private static Task<BrowserPageSource> StartAsync() =>
        BrowserPageSource.StartAsync(
            new UrlGuard(allowPrivateHosts: true),
            new BrowserPageSourceOptions { OperationBudget = TimeSpan.FromSeconds(20) },
            Token);

    private static string Row(int id, int n, string name) =>
        $"<tr class=\"chapter-row\"><td><a href=\"/fiction/7/a-book/chapter/{id}/{n}-{name}\">{n}. {name}</a></td></tr>";

    [Fact]
    public async Task Chapter_addresses_that_carry_an_id_are_numbered_by_title_not_by_id()
    {
        using var site = new LocalSite();
        site.Html("/fiction/7/a-book", "<h1>A Book</h1><table>"
            + Row(500100, 1, "arrival") + Row(500230, 2, "the-gate") + Row(500377, 3, "night")
            + "</table><a href=\"/fiction/9/other/chapter/800000/1-elsewhere\">1. Elsewhere</a>");

        await using var source = await StartAsync();
        var (_, json) = await source.ProbeAsync(site.Url("/fiction/7/a-book"), FindChapters, TimeSpan.FromSeconds(2), Token);

        var chapters = Chapters(json);
        Assert.Equal([1, 2, 3], chapters.Select(c => c.Number));
        Assert.All(chapters, c => Assert.Contains("/fiction/7/a-book/chapter/", c.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Untitled_chapters_with_ids_are_numbered_by_their_place()
    {
        using var site = new LocalSite();
        site.Html("/fiction/7/a-book", """
            <h1>A Book</h1>
            <a href="/fiction/7/a-book/chapter/31/prologue">Prologue</a>
            <a href="/fiction/7/a-book/chapter/45/the-gate">The Gate</a>
            <a href="/fiction/7/a-book/chapter/52/night">Night</a>
            """);

        await using var source = await StartAsync();
        var (_, json) = await source.ProbeAsync(site.Url("/fiction/7/a-book"), FindChapters, TimeSpan.FromSeconds(2), Token);

        Assert.Equal(["Prologue", "The Gate", "Night"], Chapters(json).OrderBy(c => c.Number).Select(c => c.Title));
    }

    [Fact]
    public async Task A_list_the_page_pages_through_itself_is_read_whole_from_the_page_as_sent()
    {
        // All rows arrive with the page; its own script then keeps only the
        // first two and pages with buttons that have no address.
        var rows = string.Concat(Enumerable.Range(1, 6).Select(n => Row(500000 + n, n, "part")));
        using var site = new LocalSite();
        site.Html("/fiction/7/a-book", "<h1>A Book</h1><table id=\"list\">" + rows + "</table>"
            + "<ul class=\"pager\"><li><a>1</a></li><li><a>2</a></li><li><a>3</a></li></ul>"
            + "<script>[...document.querySelectorAll('#list tr')].slice(2).forEach(r => r.remove());</script>");

        await using var source = await StartAsync();
        var (_, json) = await source.ProbeAsync(site.Url("/fiction/7/a-book"), FindChapters, TimeSpan.FromSeconds(2), Token);

        Assert.Equal(Enumerable.Range(1, 6), Chapters(json).Select(c => c.Number).Order());
    }

    [Fact]
    public async Task The_next_link_is_not_a_button_that_only_shares_its_style()
    {
        using var site = new LocalSite();
        site.Html("/fiction/7/a-book/chapter/2/two", """
            <header><a class="btn btn-primary adv-search" href="/search?advanced=true" aria-label="Advanced Search">?</a></header>
            <h1>2. Two</h1>
            <div class="row nav-buttons">
              <div class="col-xs-6"><a class="btn btn-primary" href="/fiction/7/a-book/chapter/1/one">Previous Chapter</a></div>
              <div class="col-xs-6"><a class="btn btn-primary" href="/fiction/7/a-book/chapter/3/three">Next Chapter</a></div>
            </div>
            <div class="chapter-inner"><p>The story, long enough to be the story of this chapter and nothing else on the page.</p>
            <p>More of the story, so the block with the most paragraph text is plainly this one.</p></div>
            <div class="row nav-buttons">
              <div class="col-xs-6"><a class="btn btn-primary" href="/fiction/7/a-book/chapter/1/one">Previous Chapter</a></div>
              <div class="col-xs-6"><a class="btn btn-primary" href="/fiction/7/a-book/chapter/3/three">Next Chapter</a></div>
            </div>
            """);

        await using var source = await StartAsync();
        var url = site.Url("/fiction/7/a-book/chapter/2/two");
        var (_, json) = await source.ProbeAsync(url, FindContent, TimeSpan.FromSeconds(2), Token);
        var next = JsonDocument.Parse(json).RootElement.GetProperty("next").GetString()!;

        var (_, followed) = await source.ProbeAsync(
            url, "() => document.querySelector(" + JsonSerializer.Serialize(next) + ").getAttribute('href')", TimeSpan.FromSeconds(1), Token);
        Assert.Equal("/fiction/7/a-book/chapter/3/three", followed);
    }

    private static List<(int Number, string Title, string Url)> Chapters(string json) =>
        [.. JsonDocument.Parse(json).RootElement.GetProperty("chapters").EnumerateArray()
            .Select(c => (c.GetProperty("number").GetInt32(), c.GetProperty("title").GetString()!, c.GetProperty("url").GetString()!))];
}

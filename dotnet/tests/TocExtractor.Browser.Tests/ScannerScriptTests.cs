using System.Globalization;
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
    public async Task A_chapter_list_behind_a_tab_is_opened_and_the_sites_count_is_read()
    {
        // Novellunar: "Synopsis | Chapters (1472)", the list loaded only
        // when the tab is pressed, fifty at a time.
        using var site = new LocalSite();
        site.Html("/novel/a-book", """
            <header><a href="/"><h1>Localhost</h1></a></header>
            <h1>A Book</h1>
            <a href="/novel/a-book/chapter/1">Read Now</a>
            <aside><a href="/novel/other/chapter/291">Chapter 291</a><a href="/novel/third/chapter/370">Chapter 370</a>
            <a href="/novel/fourth/chapter/12">Chapter 12</a></aside>
            <div><button id="syn">Synopsis</button><button id="tab">Chapters (1472)</button></div>
            <div id="body"><p>A synopsis.</p></div>
            <script>
              document.getElementById('tab').addEventListener('click', () => setTimeout(() => {
                let html = '';
                for (let n = 1; n <= 50; n++) html += `<a href="/novel/a-book/chapter/${n}">Chapter ${n} - Part</a>`;
                document.getElementById('body').innerHTML = html + '<button>1</button><button>2</button>';
              }, 600));
            </script>
            """);

        await using var source = await StartAsync();
        var (_, json) = await source.ProbeAsync(site.Url("/novel/a-book"), FindChapters, TimeSpan.FromSeconds(8), Token);

        Assert.Equal(Enumerable.Range(1, 50), Chapters(json).Select(c => c.Number).Order());
        Assert.Equal(1472, JsonDocument.Parse(json).RootElement.GetProperty("total").GetInt32());
        Assert.Equal("A Book", JsonDocument.Parse(json).RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Chapters_whose_titles_fall_behind_their_addresses_stay_in_the_book()
    {
        // novelfire: from "chapter-196" on, titles run behind the address
        // ("196 Chapter 195 Slug landing (Part 2)"); a later list page has
        // only such links, and it counted none of them.
        static string Link(int address, int titled) =>
            $"<li><a href=\"/book/a-book/chapter-{address}\"><span>{address}</span> Chapter {titled} Part</a></li>";
        var drifted = string.Concat(Enumerable.Range(201, 5).Select(n => Link(n, n - 3)));
        using var site = new LocalSite();
        site.Html("/book/a-book/chapters", "<h1>A Book</h1><ul>"
            + string.Concat(Enumerable.Range(194, 3).Select(n => Link(n, n))) + Link(197, 196) + Link(198, 196) + drifted
            + "</ul><a href=\"/book/other-book/chapter-9\">Chapter 9</a>");
        site.Html("/book/a-book/chapters-3", "<h1>A Book</h1><ul>" + drifted + "</ul>");

        await using var source = await StartAsync();
        var (_, json) = await source.ProbeAsync(site.Url("/book/a-book/chapters"), FindChapters, TimeSpan.FromSeconds(2), Token);
        var key = JsonDocument.Parse(json).RootElement.GetProperty("key").GetString()!;
        Assert.Equal([194, 195, 196, 197, 198, 201, 202, 203, 204, 205], Chapters(json).Select(c => int.Parse(c.Url[(c.Url.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture)).Order());

        var locked = "() => { window.__tocExtractorKey = " + JsonSerializer.Serialize(key) + "; return (" + FindChapters.Trim() + ")(); }";
        var (_, later) = await source.ProbeAsync(site.Url("/book/a-book/chapters-3"), locked, TimeSpan.FromSeconds(2), Token);
        Assert.Equal(5, Chapters(later).Count);
    }

    [Theory]
    [InlineData("""<div class="header-stats"><span><strong><i class="icon-book-open"></i> 938</strong><small>Chapters</small></span><span><strong>373.2K</strong><small>Views</small></span></div>""")]
    [InlineData("""<p>List of most recent chapters published for the A Book novel. A total of 938 chapters have been translated and the release date of the last chapter is Dec 1.</p>""")]
    public async Task The_count_novelfire_states_is_read(string stated)
    {
        using var site = new LocalSite();
        site.Html("/book/a-book", "<h1>A Book</h1>" + stated + "<a href=\"/book/a-book/chapter-1\">Chapter 1</a><a href=\"/book/a-book/chapter-2\">Chapter 2</a>");

        await using var source = await StartAsync();
        var (_, json) = await source.ProbeAsync(site.Url("/book/a-book"), FindChapters, TimeSpan.FromSeconds(2), Token);

        Assert.Equal(938, JsonDocument.Parse(json).RootElement.GetProperty("total").GetInt32());
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

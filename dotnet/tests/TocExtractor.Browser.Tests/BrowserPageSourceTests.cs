using TocExtractor.Core.Links;
using TocExtractor.Core.Pages;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Text;

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

    /// <summary>Built like a real reading site: the story shares its box with everything else.</summary>
    [Fact]
    public async Task Only_the_story_is_kept_from_a_cluttered_chapter()
    {
        using var site = new LocalSite();
        site.Html("/ch/7", """
            <h1 class="title">Chapter 7: The Tide<br><span class="book">A Very Long Book</span></h1>
            <div id="story">
              <div class="chapter-nav">
                <a href="/ch/6">Previous chapter</a><a href="/ch/8">Next chapter</a>
              </div>
              <p>The first line of the story.</p>
              <div class="ads"><p>BUY NOW advertisement</p></div>
              <ins class="adsbygoogle">ad slot</ins>
              <script>var tracking = "tracking script text";</script>
              <script>
                var slot = document.createElement("div");
                slot.className = "ad-banner";
                slot.textContent = "injected advertisement";
                document.currentScript.after(slot);
              </script>
              <p>The second line of the story.</p>
              <div class="share-buttons">Share on social media</div>
              <button>Report chapter</button>
              <nav><a href="/toc">Table of contents</a></nav>
              <div id="comments"><p>Great chapter, first!</p></div>
              <p>The last line of the story.</p>
            </div>
            """);

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/7"), "h1.title", "#story", Token);

        Assert.Equal("Chapter 7: The Tide", chapter.Title);
        Assert.Equal(
            ["The first line of the story.", "The second line of the story.", "The last line of the story."],
            chapter.Body.Split('\n').Where(line => line.Trim().Length > 0));
    }

    [Fact]
    public async Task A_pop_up_a_page_opens_is_closed_while_the_app_works_alone()
    {
        using var site = new LocalSite();
        site.Html("/ch/5", """
            <h1 class="t">Chapter 5</h1><article class="c"><p>Story.</p></article>
            <script>window.open('about:blank', '_blank');</script>
            """);

        await using var source = await StartAsync();
        var before = source.OpenTabs;
        var chapter = await source.LoadChapterAsync(site.Url("/ch/5"), ".t", ".c", Token);
        await Task.Delay(1000, Token);

        Assert.Equal("Story.", chapter.Body.Trim());
        Assert.Equal(before, source.OpenTabs);
    }

    [Fact]
    public async Task A_tab_closed_under_the_app_is_replaced_and_the_next_chapter_loads()
    {
        using var site = new LocalSite();
        site.Html("/ch/1", "<h1 class=\"t\">Chapter 1</h1><article class=\"c\"><p>One.</p></article>");
        site.Html("/ch/2", "<h1 class=\"t\">Chapter 2</h1><article class=\"c\"><p>Two.</p></article>");

        await using var source = await StartAsync();
        await source.LoadChapterAsync(site.Url("/ch/1"), ".t", ".c", Token);
        await source.CloseWorkTabsAsync();

        var chapter = await source.LoadChapterAsync(site.Url("/ch/2"), ".t", ".c", Token);

        Assert.Equal("Two.", chapter.Body.Trim());
        Assert.False(source.IsClosed);
    }

    [Fact]
    public async Task A_page_that_closes_its_own_tab_does_not_break_the_chapters_after_it()
    {
        using var site = new LocalSite();
        site.Html("/ch/3", """
            <h1 class="t">Chapter 3</h1><article class="c"><p>Three.</p></article>
            <script>setTimeout(() => window.close(), 50);</script>
            """);
        site.Html("/ch/4", "<h1 class=\"t\">Chapter 4</h1><article class=\"c\"><p>Four.</p></article>");

        await using var source = await StartAsync();
        await source.LoadChapterAsync(site.Url("/ch/3"), ".t", ".c", Token);
        await Task.Delay(500, Token);

        for (var i = 0; i < 3; i++)
        {
            var chapter = await source.LoadChapterAsync(site.Url("/ch/4"), ".t", ".c", Token);
            Assert.Equal("Four.", chapter.Body.Trim());
        }
    }

    [Fact]
    public async Task A_check_stays_on_its_tab_for_the_person_while_other_work_goes_on()
    {
        using var site = new LocalSite();
        site.Html("/check", """
            <html><head><title>Just a moment...</title></head>
            <body><div class="cf-turnstile"></div>Verify you are human</body></html>
            """);
        site.Html("/ch/1", "<h1 class=\"t\">Chapter 1</h1><article class=\"c\"><p>One.</p></article>");

        await using var source = await BrowserPageSource.StartAsync(
            Guard,
            new BrowserPageSourceOptions { MaxPages = 1, GrowTo = 4, OperationBudget = TimeSpan.FromSeconds(20) },
            Token);
        await Assert.ThrowsAsync<HumanCheckException>(() => source.LoadChapterAsync(site.Url("/check"), ".t", ".c", Token));

        // Other books keep working, on other tabs.
        for (var i = 0; i < 3; i++)
        {
            var chapter = await source.LoadChapterAsync(site.Url("/ch/1"), ".t", ".c", Token);
            Assert.Equal("One.", chapter.Body.Trim());
        }

        // The check was never navigated away from under the person.
        Assert.Contains(source.TabUrls, url => url.EndsWith("/check", StringComparison.Ordinal));

        // Once the wait ends, the tab goes back to work.
        Assert.False(await source.WaitForPersonAsync(TimeSpan.FromMilliseconds(100), Token));
        Assert.Equal(2, source.OpenTabs);
    }

    [Fact]
    public async Task A_page_laid_out_differently_keeps_its_chapter_under_the_pages_own_title()
    {
        using var site = new LocalSite();
        site.Html("/ch/48", "<html><head><title>Chapter 48: Odd One</title></head><body><article class=\"c\"><p>Story.</p></article></body></html>");

        await using var source = await BrowserPageSource.StartAsync(
            Guard,
            new BrowserPageSourceOptions { TitleFallback = true, OperationBudget = TimeSpan.FromSeconds(20) },
            Token);
        var chapter = await source.LoadChapterAsync(site.Url("/ch/48"), "#content > h4", ".c", Token);

        Assert.Equal("Chapter 48: Odd One", chapter.Title);
        Assert.Equal("Story.", chapter.Body.Trim());
    }

    [Fact]
    public async Task Without_the_fallback_a_missing_title_still_fails()
    {
        using var site = new LocalSite();
        site.Html("/ch/48", "<html><head><title>Chapter 48</title></head><body><article class=\"c\"><p>Story.</p></article></body></html>");

        await using var source = await BrowserPageSource.StartAsync(
            Guard, new BrowserPageSourceOptions { OperationBudget = TimeSpan.FromSeconds(3) }, Token);

        await Assert.ThrowsAsync<SelectorNotFoundException>(() => source.LoadChapterAsync(site.Url("/ch/48"), "#content > h4", ".c", Token));
    }

    [Fact]
    public async Task With_room_to_grow_busy_tabs_open_another_rather_than_wait()
    {
        using var site = new LocalSite();
        site.Html("/slow", """
            <h1 class="t">Slow</h1><article class="c"></article>
            <script>setTimeout(() => document.querySelector('.c').innerHTML = '<p>Late.</p>', 800);</script>
            """);

        await using var source = await BrowserPageSource.StartAsync(
            Guard,
            new BrowserPageSourceOptions { MaxPages = 1, GrowTo = 3, OperationBudget = TimeSpan.FromSeconds(20) },
            Token);
        var loads = Enumerable.Range(0, 3)
            .Select(_ => source.ProbeAsync(site.Url("/slow"), "() => document.querySelector('.c').innerText", TimeSpan.FromSeconds(3), Token))
            .ToList();
        var answers = await Task.WhenAll(loads);

        Assert.All(answers, answer => Assert.Equal("Late.", answer.Json.Trim()));
        Assert.Equal(3, source.OpenTabs);
    }

    /// <summary>A made-up paragraph, distinct for each number, as long as a story's.</summary>
    private static string Paragraph(int n) =>
        $"Paragraph {n} begins, and the keeper climbs the stair while the tide turns below the rocks and the lamp burns on.";

    [Fact]
    public async Task Every_paragraph_of_a_cluttered_chapter_is_saved_once_in_order()
    {
        var html = new System.Text.StringBuilder("<h1 class=\"t\">Chapter 5</h1><div class=\"c\">");
        html.Append("<div class=\"chapter-nav\"><a href=\"/4\">Prev</a> <a href=\"/6\">Next chapter</a></div>");
        for (var n = 1; n <= 60; n++)
        {
            var text = Paragraph(n);
            html.Append(n switch
            {
                _ when n % 10 == 0 => $"<div class=\"ads\"><ins>Advertisement</ins><script>void 0;</script></div><p>{text}</p>",
                _ when n % 7 == 0 => $"<p>{text[..20]}<em>{text[20..40]}</em><a href=\"/x\">{text[40..50]}</a>{text[50..]}</p>",
                _ when n % 9 == 0 => $"<p>{text[..30]}\u200B{text[30..]}</p>",
                _ when n % 11 == 0 => $"<div><span>{text[..45]}</span><span>{text[45..]}</span></div>",
                _ => $"<p>{text}</p>",
            });
        }

        html.Append("<div style=\"display:none\">Hidden from readers.</div>");
        html.Append("<div class=\"share-buttons\">Share to your friends</div></div>");
        using var site = new LocalSite();
        site.Html("/ch/5", html.ToString());

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/5"), ".t", ".c", Token);

        // Every paragraph, exactly once, in order.
        var at = -1;
        for (var n = 1; n <= 60; n++)
        {
            var found = chapter.Body.IndexOf(Paragraph(n), StringComparison.Ordinal);
            Assert.True(found > at, $"paragraph {n} missing or out of order");
            Assert.Equal(found, chapter.Body.LastIndexOf(Paragraph(n), StringComparison.Ordinal));
            at = found;
        }

        Assert.DoesNotContain("Advertisement", chapter.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Next chapter", chapter.Body, StringComparison.Ordinal);

        // And the chapter's own check agrees: nothing left out, nothing twice.
        Assert.NotNull(chapter.PageText);
        var audit = TextAudit.Compare(chapter.PageText, chapter.Body);
        Assert.False(audit.LeftOutStory);
        Assert.False(audit.DoubledText);
    }

    [Fact]
    public async Task A_story_paragraph_the_cleanup_removes_by_mistake_is_caught()
    {
        // A site that happens to style a story paragraph with a class the ad
        // and menu cleanup matches ("notice"). The cleanup removes it; the
        // check must say so, so the person is told rather than never knowing.
        using var site = new LocalSite();
        site.Html("/ch/6", $"""
            <h1 class="t">Chapter 6</h1><div class="c">
            <p>{Paragraph(1)}</p><div class="notice">{Paragraph(2)}</div><p>{Paragraph(3)}</p></div>
            """);

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/6"), ".t", ".c", Token);

        Assert.DoesNotContain(Paragraph(2), chapter.Body, StringComparison.Ordinal);
        var audit = TextAudit.Compare(chapter.PageText!, chapter.Body);
        Assert.True(audit.LeftOutStory);
        Assert.Equal([Paragraph(2)], audit.LeftOutLong);
    }

    [Fact]
    public async Task An_ad_frame_that_redirects_does_not_become_the_chapters_address()
    {
        using var site = new LocalSite();
        site.Redirect("/ad", "/ad/landing");
        site.Html("/ad/landing", "<p>an advert</p>");
        site.Html("/ch/3", """
            <html><body><iframe src="/ad"></iframe>
            <h1 class="t">Chapter 3</h1><article class="c"><p>Three.</p></article>
            <script>const until = Date.now() + 1500; while (Date.now() < until) {}</script>
            </body></html>
            """);

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/3"), ".t", ".c", Token);

        Assert.Equal("Three.", chapter.Body.Trim());
        Assert.Equal(site.Url("/ch/3"), chapter.FinalUrl);
        lock (site.Requested)
        {
            // The frames were still loaded, and screened hop by hop.
            Assert.Contains("/ad/landing", site.Requested);
        }
    }

    [Fact]
    public async Task Light_pages_skip_pictures_and_fonts_but_keep_style_sheets()
    {
        using var site = new LocalSite();
        site.Html("/ch/1", """
            <html><head><link rel="stylesheet" href="/look.css"></head><body>
            <h1 class="t">Chapter 1</h1><img src="/picture.png">
            <article class="c"><p>One.</p></article></body></html>
            """);
        site.Html("/look.css", "p { color: black; }");

        await using var light = await BrowserPageSource.StartAsync(
            Guard, new BrowserPageSourceOptions { LightPages = true, OperationBudget = TimeSpan.FromSeconds(20) }, Token);
        var chapter = await light.LoadChapterAsync(site.Url("/ch/1"), ".t", ".c", Token);

        Assert.Equal("One.", chapter.Body.Trim());
        await Task.Delay(300, Token);
        lock (site.Requested)
        {
            Assert.Contains("/look.css", site.Requested);
            Assert.DoesNotContain("/picture.png", site.Requested);
        }

        // While the person uses the window, pages load whole.
        light.PersonArrived();
        await light.OpenPageAsync(site.Url("/ch/1"), Token);
        await Task.Delay(300, Token);
        light.PersonLeft();
        lock (site.Requested)
        {
            Assert.Contains("/picture.png", site.Requested);
        }
    }

    [Fact]
    public async Task Idle_tabs_close_down_to_one_and_the_last_is_emptied()
    {
        using var site = new LocalSite();
        site.Html("/slow", """
            <h1 class="t">Slow</h1><article class="c"></article>
            <script>setTimeout(() => document.querySelector('.c').innerHTML = '<p>Late.</p>', 800);</script>
            """);

        await using var source = await BrowserPageSource.StartAsync(
            Guard,
            new BrowserPageSourceOptions { MaxPages = 1, GrowTo = 3, OperationBudget = TimeSpan.FromSeconds(20) },
            Token);
        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => source.ProbeAsync(site.Url("/slow"), "() => document.querySelector('.c').innerText", TimeSpan.FromSeconds(3), Token)));
        Assert.Equal(3, source.WorkTabs);

        await source.TidyAsync(TimeSpan.Zero, Token);
        Assert.Equal(1, source.WorkTabs);
        Assert.Equal(1, source.OpenTabs);

        // Left waiting, the one tab's page is emptied, so nothing on it runs.
        await Task.Delay(TimeSpan.FromSeconds(3.2), Token);
        await source.TidyAsync(TimeSpan.FromHours(1), Token);
        Assert.Equal(["about:blank"], source.TabUrls);

        // And it still works, as does growing again.
        var again = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => source.ProbeAsync(site.Url("/slow"), "() => document.querySelector('.c').innerText", TimeSpan.FromSeconds(3), Token)));
        Assert.All(again, answer => Assert.Equal("Late.", answer.Json.Trim()));
        Assert.Equal(2, source.WorkTabs);
    }

    [Fact]
    public async Task Short_of_memory_work_waits_for_an_open_tab_instead_of_opening_more()
    {
        using var site = new LocalSite();
        site.Html("/slow", """
            <h1 class="t">Slow</h1><article class="c"></article>
            <script>setTimeout(() => document.querySelector('.c').innerHTML = '<p>Late.</p>', 800);</script>
            """);

        await using var source = await BrowserPageSource.StartAsync(
            Guard,
            new BrowserPageSourceOptions
            {
                MaxPages = 1,
                GrowTo = 4,
                MemoryTight = () => true,
                OperationBudget = TimeSpan.FromSeconds(20),
            },
            Token);
        var answers = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => source.ProbeAsync(site.Url("/slow"), "() => document.querySelector('.c').innerText", TimeSpan.FromSeconds(3), Token)));

        Assert.All(answers, answer => Assert.Equal("Late.", answer.Json.Trim()));
        Assert.Equal(1, source.WorkTabs);
    }

    [Fact]
    public async Task A_chapter_locked_to_visitors_is_refused_not_half_saved()
    {
        using var site = new LocalSite();
        site.Html("/ch/9", """
            <h1 class="t">Chapter 9: Halfway</h1>
            <article class="c"><p>The first half of the story.</p></article>
            <section class="wall"><h3>Continue Reading</h3>
            <p>Login to access the full chapter content</p>
            <a href="/login">Login to Continue</a></section>
            """);

        await using var source = await StartAsync();
        var locked = await Assert.ThrowsAsync<HumanCheckException>(
            () => source.LoadChapterAsync(site.Url("/ch/9"), ".t", ".c", Token));

        Assert.True(locked.NeedsSignIn);
    }

    [Fact]
    public async Task A_story_that_mentions_logging_in_is_not_locked()
    {
        using var site = new LocalSite();
        site.Html("/ch/10", "<h1 class='t'>Chapter 10</h1><article class='c'><p>He had to log in to read the files.</p></article>");

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/10"), ".t", ".c", Token);

        Assert.Contains("log in to read the files", chapter.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_security_check_page_is_recognised()
    {
        using var site = new LocalSite();
        site.Html("/ch/11", """
            <title>Just a moment...</title>
            <h2>Security check</h2><p>Our system has detected abnormal activity from your IP address.</p>
            <div class="cf-turnstile" data-sitekey="x"></div>
            """);

        await using var source = await StartAsync();
        var check = await Assert.ThrowsAsync<HumanCheckException>(
            () => source.LoadChapterAsync(site.Url("/ch/11"), ".t", ".c", Token));

        Assert.False(check.NeedsSignIn);
    }

    [Fact]
    public async Task A_long_chapter_that_mentions_a_security_check_is_just_a_chapter()
    {
        using var site = new LocalSite();
        var story = string.Concat(Enumerable.Repeat("<p>The guard ran a security check on every visitor at the gate. </p>", 150));
        site.Html("/ch/12", $"<h1 class='t'>Chapter 12</h1><article class='c'>{story}</article>");

        await using var source = await StartAsync();
        var chapter = await source.LoadChapterAsync(site.Url("/ch/12"), ".t", ".c", Token);

        Assert.StartsWith("The guard ran a security check", chapter.Body, StringComparison.Ordinal);
    }

    /// <summary>Many sites fetch the chapter list with a second request once the page is up.</summary>
    [Fact]
    public async Task A_chapter_list_that_arrives_late_is_still_read()
    {
        using var site = new LocalSite();
        site.Html("/toc/late", """
            <ol id="list"></ol>
            <script>
            setTimeout(function () {
              var list = document.getElementById('list');
              for (var i = 1; i <= 3; i++) {
                var item = document.createElement('li');
                item.innerHTML = '<a class="ch" href="/ch/' + i + '">Chapter ' + i + '</a>';
                list.appendChild(item);
              }
            }, 800);
            </script>
            """);

        await using var source = await StartAsync();
        var toc = await source.LoadTocAsync(site.Url("/toc/late"), "a.ch", cancellationToken: Token);

        Assert.Equal(3, toc.RawLinks.Count);
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

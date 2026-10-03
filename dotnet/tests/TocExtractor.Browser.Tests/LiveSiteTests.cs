using TocExtractor.Core.Politeness;

namespace TocExtractor.Browser.Tests;

/// <summary>Chapters from real sites that once went wrong. Off unless TOC_LIVE is set: they need the internet.</summary>
public sealed class LiveSiteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool Live => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TOC_LIVE"));

    /// <summary>Chapter 8 says "tensed for just a moment" and was taken for a check page.</summary>
    [Fact]
    public async Task Novelfire_chapter_8_of_supreme_daily_login_system_is_a_chapter()
    {
        Assert.SkipUnless(Live, "Set TOC_LIVE=1 to read real sites.");
        await using var source = await BrowserPageSource.StartAsync(
            new UrlGuard(), new BrowserPageSourceOptions { OperationBudget = TimeSpan.FromSeconds(45) }, Token);

        foreach (var n in new[] { 7, 8, 9 })
        {
            var chapter = await source.LoadChapterAsync(
                $"https://novelfire.net/book/supreme-daily-login-system/chapter-{n}", "span.chapter-title", "#content", Token);
            Assert.Contains($"Chapter {n}", chapter.Title, StringComparison.Ordinal);
            Assert.True(chapter.Body.Length > 1000, $"chapter {n}: {chapter.Body.Length} characters");
            await Task.Delay(1500, Token);
        }
    }

    /// <summary>What a page looks like to a plain Playwright Chromium: its status, title and whether it reads as a check.</summary>
    [Fact]
    public async Task Probe_a_page()
    {
        var url = Environment.GetEnvironmentVariable("TOC_PROBE");
        Assert.SkipUnless(Live && url is { Length: > 0 }, "Set TOC_LIVE=1 and TOC_PROBE=<url>.");
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var headful = Environment.GetEnvironmentVariable("TOC_LIVE_HEADFUL") is { Length: > 0 };
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = !headful });
        var page = await browser.NewPageAsync();
        var response = await page.GotoAsync(url!);
        await Task.Delay(6000, Token);
        var json = await page.EvaluateAsync<string>(
            "() => JSON.stringify({ title: document.title, check: (" + BrowserPageSource.HumanCheckScript + ")(), length: (document.body ? document.body.innerText.length : 0), widgets: [...document.querySelectorAll('iframe[src*=\"challenges.cloudflare.com\"], .cf-turnstile, #challenge-form, #cf-challenge-running, .g-recaptcha, .h-captcha, [data-sitekey]')].map(e => e.outerHTML.slice(0, 300)), text: (document.body ? document.body.innerText.slice(0, 800) : '') })");
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "toc-probe.json"), $"{response?.Status} {page.Url}\n{json}", Token);
    }
}

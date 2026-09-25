namespace TocExtractor.App.Session;

/// <summary>What stands between the browser and the chapter list, if anything.</summary>
public enum Obstacle
{
    None,

    /// <summary>A "verify you are human" page, such as a Cloudflare check.</summary>
    HumanCheck,

    /// <summary>A sign-in form.</summary>
    SignIn,
}

/// <summary>
/// Recognises the pages a site shows instead of its content, so an empty
/// result can say what to do rather than just "no chapters found".
/// </summary>
/// <remarks>
/// This only recognises them. Getting past one is the person's job, in the
/// visible browser, which is what the sign-in step is for. It is consulted
/// only when the link selector found nothing: plenty of real pages carry a
/// challenge script or a login box in the header while serving content fine.
/// </remarks>
public static class PageCheck
{
    private static readonly string[] HumanCheckMarkers =
    [
        "Just a moment...",
        "cf-chl",
        "challenges.cloudflare.com",
        "Checking your browser",
        "Verify you are human",
        "verify you are a human",
        "g-recaptcha",
        "h-captcha",
        "DDoS protection by",
    ];

    public static Obstacle Detect(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return Obstacle.None;
        }

        if (HumanCheckMarkers.Any(marker => html.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return Obstacle.HumanCheck;
        }

        return html.Contains("type=\"password\"", StringComparison.OrdinalIgnoreCase)
            || html.Contains("type='password'", StringComparison.OrdinalIgnoreCase)
            || html.Contains("type=password", StringComparison.OrdinalIgnoreCase)
            ? Obstacle.SignIn
            : Obstacle.None;
    }

    public static string Advice(Obstacle obstacle) => obstacle switch
    {
        Obstacle.HumanCheck =>
            "The site is showing a \"verify you're human\" check. Complete it in the browser window, then press Test selectors again.",
        Obstacle.SignIn =>
            "The site is asking you to sign in. Sign in in the browser window, then press Test selectors again.",
        _ => "The link selector matched no chapter links on the contents page.",
    };
}

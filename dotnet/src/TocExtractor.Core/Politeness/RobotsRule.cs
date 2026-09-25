using System.Globalization;
using System.Text.RegularExpressions;

namespace TocExtractor.Core.Politeness;

/// <summary>One Allow or Disallow directive, with the line it came from.</summary>
/// <remarks>
/// The line number is carried because the post-gate warning has to name the
/// rule it is overriding. "robots.txt disallows this" sends a reader hunting;
/// naming the directive and its line does not.
/// </remarks>
public sealed partial class RobotsRule
{
    private readonly Regex? matcher;
    private readonly bool anchored;

    internal RobotsRule(string directive, string value, int lineNumber, string userAgent)
    {
        this.Directive = directive;
        this.Value = value;
        this.LineNumber = lineNumber;
        this.UserAgent = userAgent;

        // An empty Disallow means "allow everything", not "disallow everything".
        this.Allows = directive == "allow" || value.Length == 0;

        var path = RepeatedStars().Replace(value, "*");
        path = RepeatedAnchors().Replace(path, "$");

        this.anchored = path.EndsWith('$');
        path = path.TrimEnd('$');
        this.Pattern = path;

        if (path.Contains('*', StringComparison.Ordinal))
        {
            this.matcher = new Regex(
                Translate(path), RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }
    }

    /// <summary>"allow" or "disallow", as written.</summary>
    public string Directive { get; }

    /// <summary>The path as written, before wildcard normalisation.</summary>
    public string Value { get; }

    public int LineNumber { get; }

    /// <summary>The group's user agent, for the message this rule appears in.</summary>
    public string UserAgent { get; }

    internal bool Allows { get; }

    internal string Pattern { get; }

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"\"{this.Directive}: {this.Value}\" at robots.txt line {this.LineNumber} (User-agent: {this.UserAgent})");

    /// <summary>
    /// How specifically this rule matches, or zero when it does not.
    /// </summary>
    /// <remarks>
    /// The length plus one, so that a zero-length match on an empty path is
    /// still distinguishable from no match at all. Longer wins, which is what
    /// lets <c>Allow: /members/public/</c> override <c>Disallow: /members/</c>
    /// wherever it appears in the file.
    /// </remarks>
    internal int Specificity(string path)
    {
        if (this.matcher is not null)
        {
            var match = this.matcher.Match(path);
            if (!match.Success)
            {
                return 0;
            }

            return this.anchored && match.Length != path.Length ? 0 : match.Length + 1;
        }

        if (this.anchored)
        {
            return string.Equals(path, this.Pattern, StringComparison.Ordinal)
                ? this.Pattern.Length + 1
                : 0;
        }

        return path.StartsWith(this.Pattern, StringComparison.Ordinal)
            ? this.Pattern.Length + 1
            : 0;
    }

    /// <summary>Turn a robots path containing <c>*</c> into a regular expression.</summary>
    private static string Translate(string path)
    {
        var parts = path.Split('*').Select(Regex.Escape).ToArray();
        for (var i = 1; i < parts.Length - 1; i++)
        {
            parts[i] = $"(?>.*?{parts[i]})";
        }

        parts[^1] = ".*" + parts[^1];
        return string.Concat(parts);
    }

    [GeneratedRegex(@"\*{2,}")]
    private static partial Regex RepeatedStars();

    [GeneratedRegex(@"\$[$*]+")]
    private static partial Regex RepeatedAnchors();
}

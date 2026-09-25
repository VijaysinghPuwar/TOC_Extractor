namespace TocExtractor.Core.Links;

/// <summary>The three selectors the user supplies. No site defaults, ever.</summary>
public sealed record SelectorSet
{
    private SelectorSet(string link, string title, string content, IReadOnlyList<string> missing)
    {
        this.Link = link;
        this.Title = title;
        this.Content = content;
        this.Missing = missing;
    }

    public string Link { get; }

    public string Title { get; }

    public string Content { get; }

    /// <summary>The names of the selectors that were left blank.</summary>
    public IReadOnlyList<string> Missing { get; }

    public bool Complete => this.Missing.Count == 0;

    public static SelectorSet Create(string link, string title, string content)
    {
        (string Name, string Value)[] supplied =
            [("link", link ?? ""), ("title", title ?? ""), ("content", content ?? "")];

        string[] missing = [.. supplied
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Name)];

        return new SelectorSet(
            supplied[0].Value.Trim(), supplied[1].Value.Trim(), supplied[2].Value.Trim(), missing);
    }
}

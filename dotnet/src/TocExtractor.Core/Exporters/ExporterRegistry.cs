using TocExtractor.Core.Models;
using TocExtractor.Core.Sinks;

namespace TocExtractor.Core.Exporters;

/// <summary>Output formats, addressed by name.</summary>
/// <remarks>
/// A dictionary rather than a plugin system. Three formats in a single-purpose
/// tool do not need one, and it would be more code than the exporters it
/// dispatches to.
/// </remarks>
public static class ExporterRegistry
{
    public const string DefaultFormat = TextExporter.FormatName;

    private static readonly Dictionary<string, Func<string, bool, IReadOnlyDictionary<string, PriorChapter>?, ISink>> Factories =
        new(StringComparer.Ordinal)
        {
            [TextExporter.FormatName] = (dir, links, resumed) => new TextExporter(dir, links, resumed),
            [MarkdownExporter.FormatName] = (dir, links, resumed) => new MarkdownExporter(dir, links, resumed),
            [JsonlExporter.FormatName] = (dir, _, resumed) => new JsonlExporter(dir, resumed),
        };

    public static IReadOnlyList<string> Available => [.. Factories.Keys.Order(StringComparer.Ordinal)];

    /// <summary>One sink for the requested formats, deduplicated, in a stable order.</summary>
    public static ISink Build(
        IReadOnlyList<string> formats,
        string outputDirectory,
        bool includeLinks = false,
        IReadOnlyDictionary<string, PriorChapter>? resumed = null)
    {
        ArgumentNullException.ThrowIfNull(formats);

        List<string> chosen = [];
        foreach (var name in formats.Count == 0 ? [DefaultFormat] : formats)
        {
            if (!Factories.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"unknown format '{name}'; available: {string.Join(", ", Available)}",
                    nameof(formats));
            }

            if (!chosen.Contains(name, StringComparer.Ordinal))
            {
                chosen.Add(name);
            }
        }

        var sinks = chosen.Select(name => Factories[name](outputDirectory, includeLinks, resumed)).ToArray();
        return sinks.Length == 1 ? sinks[0] : new MultiSink(sinks);
    }
}

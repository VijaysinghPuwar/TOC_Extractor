using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TocExtractor.Core.Exporters;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;
using TocExtractor.Core.Sinks;

namespace TocExtractor.Core.Tests.Exporters;

public sealed class ExporterTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "toc-export-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ChapterRecord Record(int index, string url, string title, string text) =>
        new(index, url, url, title, text, 0, DateTimeOffset.UnixEpoch, 1);

    private static RunResult Result(IReadOnlyList<string> kept, params ChapterRecord[] completed) =>
        new(
            "https://e.com/toc",
            new LinkTally(kept.Count, kept),
            completed,
            SkippedResumed: [.. kept.Where(url => !completed.Any(r => r.RequestedUrl == url))]);

    private string Read(string name) => File.ReadAllText(Path.Combine(this.directory, name));

    // -- registry ------------------------------------------------------------

    [Fact]
    public void Available_formats_are_stable() =>
        Assert.Equal(["jsonl", "markdown", "text"], ExporterRegistry.Available);

    [Fact]
    public void Default_is_text_when_nothing_is_asked_for() =>
        Assert.IsType<TextExporter>(ExporterRegistry.Build([], this.directory));

    [Fact]
    public void Several_formats_fan_out()
    {
        var sink = ExporterRegistry.Build(["text", "jsonl"], this.directory);

        Assert.Equal(2, Assert.IsType<MultiSink>(sink).Sinks.Count);
    }

    [Fact]
    public void Repeated_format_is_deduplicated() =>
        Assert.IsType<TextExporter>(ExporterRegistry.Build(["text", "text"], this.directory));

    [Fact]
    public void Unknown_format_names_the_alternatives()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => ExporterRegistry.Build(["epub"], this.directory));

        Assert.Contains("markdown", thrown.Message, StringComparison.Ordinal);
    }

    // -- text ----------------------------------------------------------------

    [Fact]
    public async Task Chapter_file_and_combined_have_the_expected_layout()
    {
        var exporter = new TextExporter(this.directory);
        var record = Record(1, "https://e.com/a", "One", "Body one.");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(record, Token);
        await exporter.CloseAsync(Result(["https://e.com/a"], record), Token);

        Assert.Equal("One\n\nBody one.\n", this.Read("001 - One.txt"));
        Assert.Equal("One\n\nBody one.\n\n" + new string('-', 80) + "\n\n", this.Read("combined.txt"));
    }

    [Fact]
    public async Task Include_links_adds_a_source_line()
    {
        var exporter = new TextExporter(this.directory, includeLinks: true);
        var record = Record(1, "https://e.com/a", "One", "Body.");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(record, Token);
        await exporter.CloseAsync(Result(["https://e.com/a"], record), Token);

        Assert.Contains("Source: https://e.com/a", this.Read("001 - One.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Combined_follows_the_table_of_contents_regardless_of_write_order()
    {
        var exporter = new TextExporter(this.directory);
        var first = Record(1, "https://e.com/a", "One", "A");
        var second = Record(2, "https://e.com/b", "Two", "B");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(second, Token);
        await exporter.WriteAsync(first, Token);
        await exporter.CloseAsync(Result(["https://e.com/a", "https://e.com/b"], first, second), Token);

        var combined = this.Read("combined.txt");
        Assert.True(
            combined.IndexOf("One", StringComparison.Ordinal)
            < combined.IndexOf("Two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Case_colliding_titles_get_distinct_files()
    {
        var exporter = new TextExporter(this.directory);
        var first = Record(1, "https://e.com/a", "Chapter One", "A");
        var second = Record(1, "https://e.com/b", "chapter one", "B");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(first, Token);
        await exporter.WriteAsync(second, Token);

        Assert.Single(exporter.Deduplicated);
        Assert.True(File.Exists(Path.Combine(this.directory, "001 - chapter one (2).txt")));
    }

    // -- resume, and the four defects ----------------------------------------

    private async Task<PriorChapter> WritePriorChapter(
        string url, int index, string title, string body, string format = "text")
    {
        Directory.CreateDirectory(this.directory);
        var name = format == "text" ? $"{index:D3} - {title}.txt" : $"{index:D3} - {title}.md";
        var content = format == "text" ? $"{title}\n\n{body}\n" : $"# {title}\n\n{body}\n";
        await File.WriteAllTextAsync(Path.Combine(this.directory, name), content, Token);

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new PriorChapter(
            index, url, title, body.Length, "", 0, "",
            new Dictionary<string, ChapterOutput>(StringComparer.Ordinal)
            {
                [format] = new ChapterOutput(name, hash),
            });
    }

    /// <summary>
    /// Bug: after chapters are prepended, a fresh chapter takes index 1 while a
    /// resumed chapter also holds index 1 from the earlier run. Merging by index
    /// skips the resumed one as "already present" and it vanishes from
    /// combined.txt while sitting on disk. Merging by URL cannot collide.
    /// </summary>
    [Fact]
    public async Task Prepended_chapter_does_not_displace_a_resumed_one()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old One", "old body");
        var resumed = new Dictionary<string, PriorChapter>(UrlIdentity.Comparer)
        {
            ["https://e.com/old"] = prior,
        };

        var exporter = new TextExporter(this.directory, resumed: resumed);
        var fresh = Record(1, "https://e.com/new", "New One", "new body");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);
        await exporter.CloseAsync(
            Result(["https://e.com/new", "https://e.com/old"], fresh), Token);

        var combined = this.Read("combined.txt");
        Assert.Contains("New One", combined, StringComparison.Ordinal);
        Assert.Contains("Old One", combined, StringComparison.Ordinal);
        // Table-of-contents order, not index order: the prepended chapter first.
        Assert.True(
            combined.IndexOf("New One", StringComparison.Ordinal)
            < combined.IndexOf("Old One", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deleted_chapter_file_is_refused_not_quietly_dropped()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old", "body");
        File.Delete(Path.Combine(this.directory, prior.Output("text")!.Name));

        var exporter = new TextExporter(
            this.directory,
            resumed: new Dictionary<string, PriorChapter>(UrlIdentity.Comparer) { ["https://e.com/old"] = prior });
        var fresh = Record(2, "https://e.com/new", "New", "new");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);

        await Assert.ThrowsAsync<MergeIncompleteException>(
            () => exporter.CloseAsync(Result(["https://e.com/old", "https://e.com/new"], fresh), Token));
    }

    [Fact]
    public async Task An_edited_chapter_file_is_refused()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old", "body");
        await File.WriteAllTextAsync(
            Path.Combine(this.directory, prior.Output("text")!.Name), "tampered", Token);

        var exporter = new TextExporter(
            this.directory,
            resumed: new Dictionary<string, PriorChapter>(UrlIdentity.Comparer) { ["https://e.com/old"] = prior });
        var fresh = Record(2, "https://e.com/new", "New", "new");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);

        var thrown = await Assert.ThrowsAsync<MergeIncompleteException>(
            () => exporter.CloseAsync(Result(["https://e.com/old", "https://e.com/new"], fresh), Token));
        Assert.Contains("changed since they were written", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unchanged_chapter_file_passes_verification()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old", "body");
        var exporter = new TextExporter(
            this.directory,
            resumed: new Dictionary<string, PriorChapter>(UrlIdentity.Comparer) { ["https://e.com/old"] = prior });
        var fresh = Record(2, "https://e.com/new", "New", "new");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);
        await exporter.CloseAsync(Result(["https://e.com/old", "https://e.com/new"], fresh), Token);

        Assert.Contains("Old", this.Read("combined.txt"), StringComparison.Ordinal);
    }

    /// <summary>Stray files in the directory must never be swept into the book.</summary>
    [Fact]
    public async Task Stray_files_are_never_merged()
    {
        Directory.CreateDirectory(this.directory);
        await File.WriteAllTextAsync(
            Path.Combine(this.directory, "999 - Someone Else's Book.txt"), "not mine", Token);

        var exporter = new TextExporter(this.directory);
        var record = Record(1, "https://e.com/a", "One", "A");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(record, Token);
        await exporter.CloseAsync(Result(["https://e.com/a"], record), Token);

        Assert.DoesNotContain("not mine", this.Read("combined.txt"), StringComparison.Ordinal);
    }

    // -- markdown ------------------------------------------------------------

    [Fact]
    public async Task Markdown_writes_a_heading_and_a_book()
    {
        var exporter = new MarkdownExporter(this.directory);
        var record = Record(1, "https://e.com/a", "One", "Body.");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(record, Token);
        await exporter.CloseAsync(Result(["https://e.com/a"], record), Token);

        Assert.Equal("# One\n\nBody.\n", this.Read("001 - One.md"));
        Assert.Equal("# One\n\nBody.\n\n", this.Read("book.md"));
    }

    [Fact]
    public void Markdown_title_cannot_reopen_the_document_structure() =>
        Assert.Equal("Prologue", MarkdownExporter.EscapeHeading("# Prologue"));

    /// <summary>
    /// Bug: markdown skipped a resumed chapter whose file it could not find and
    /// wrote a shorter book with no error, while the text exporter refused. It
    /// also guessed its filename from the text exporter's, so choosing markdown
    /// alone recorded nothing to guess from and lost every resumed chapter.
    /// </summary>
    [Fact]
    public async Task Markdown_refuses_a_missing_resumed_chapter_rather_than_shortening_the_book()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old", "body", format: "markdown");
        File.Delete(Path.Combine(this.directory, prior.Output("markdown")!.Name));

        var exporter = new MarkdownExporter(
            this.directory,
            resumed: new Dictionary<string, PriorChapter>(UrlIdentity.Comparer) { ["https://e.com/old"] = prior });
        var fresh = Record(2, "https://e.com/new", "New", "new");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);

        await Assert.ThrowsAsync<MergeIncompleteException>(
            () => exporter.CloseAsync(Result(["https://e.com/old", "https://e.com/new"], fresh), Token));
    }

    [Fact]
    public async Task Markdown_resumes_from_its_own_recorded_filename()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old", "body", format: "markdown");
        var exporter = new MarkdownExporter(
            this.directory,
            resumed: new Dictionary<string, PriorChapter>(UrlIdentity.Comparer) { ["https://e.com/old"] = prior });
        var fresh = Record(2, "https://e.com/new", "New", "new");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);
        await exporter.CloseAsync(Result(["https://e.com/old", "https://e.com/new"], fresh), Token);

        Assert.Contains("# Old", this.Read("book.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_exporter_reports_the_file_it_wrote()
    {
        var sink = ExporterRegistry.Build(["text", "markdown"], this.directory);
        var record = Record(1, "https://e.com/a", "One", "A");

        await sink.OpenAsync(Token);
        await sink.WriteAsync(record, Token);

        var outputs = sink.OutputsFor(1);
        Assert.Equal("001 - One.txt", outputs["text"].Name);
        Assert.Equal("001 - One.md", outputs["markdown"].Name);
    }

    // -- manifest ------------------------------------------------------------

    [Fact]
    public async Task Manifest_carries_every_required_field_and_ends_with_a_summary()
    {
        var exporter = new JsonlExporter(this.directory);
        var record = Record(1, "https://e.com/a", "One", "A");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(record, Token);
        await exporter.CloseAsync(Result(["https://e.com/a"], record), Token);

        var lines = this.Read("manifest.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var chapter = JsonDocument.Parse(lines[0]).RootElement;
        var summary = JsonDocument.Parse(lines[^1]).RootElement;

        Assert.Equal("chapter", chapter.GetProperty("type").GetString());
        Assert.Equal("One", chapter.GetProperty("title").GetString());
        Assert.Equal(record.Sha256, chapter.GetProperty("sha256").GetString());
        Assert.Equal("summary", summary.GetProperty("type").GetString());
        Assert.Equal(1, summary.GetProperty("completed").GetInt32());
    }

    [Fact]
    public async Task Every_manifest_line_is_valid_json()
    {
        var exporter = new JsonlExporter(this.directory);
        var record = Record(1, "https://e.com/a", "Ünïcödé — 中文", "A");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(record, Token);
        await exporter.CloseAsync(Result(["https://e.com/a"], record), Token);

        foreach (var line in this.Read("manifest.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var parsed = JsonDocument.Parse(line);
            Assert.NotEqual(JsonValueKind.Undefined, parsed.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// The manifest has the same index-collision problem as the merged text, and
    /// for the same reason. A resumed chapter must survive a prepend here too.
    /// </summary>
    [Fact]
    public async Task Manifest_keeps_a_resumed_chapter_through_a_prepend()
    {
        var prior = await this.WritePriorChapter("https://e.com/old", 1, "Old One", "body");
        var exporter = new JsonlExporter(
            this.directory,
            new Dictionary<string, PriorChapter>(UrlIdentity.Comparer) { ["https://e.com/old"] = prior });
        var fresh = Record(1, "https://e.com/new", "New One", "new");

        await exporter.OpenAsync(Token);
        await exporter.WriteAsync(fresh, Token);
        await exporter.CloseAsync(Result(["https://e.com/new", "https://e.com/old"], fresh), Token);

        var text = this.Read("manifest.jsonl");
        Assert.Contains("New One", text, StringComparison.Ordinal);
        Assert.Contains("Old One", text, StringComparison.Ordinal);
        Assert.Contains("from_checkpoint", text, StringComparison.Ordinal);
    }
}

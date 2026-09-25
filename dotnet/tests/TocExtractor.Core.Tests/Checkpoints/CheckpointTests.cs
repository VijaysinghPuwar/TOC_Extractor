using TocExtractor.Core.Checkpoints;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;

namespace TocExtractor.Core.Tests.Checkpoints;

public sealed class CheckpointTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "toc-ckpt-" + Guid.NewGuid().ToString("N"));

    private static readonly SelectorSet Selectors = SelectorSet.Create("a.ch", "h1", "article");

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private Checkpoint New(string tocUrl = "https://e.com/toc", params string[] links)
    {
        Directory.CreateDirectory(this.directory);
        return new Checkpoint
        {
            Path = Checkpoint.PathFor(this.directory),
            TocUrl = tocUrl,
            Fingerprint = Checkpoint.FingerprintOf(tocUrl, Selectors),
            Selectors = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["link"] = Selectors.Link,
                ["title"] = Selectors.Title,
                ["content"] = Selectors.Content,
            },
            LinkSet = [.. links],
        };
    }

    private static ChapterRecord Record(int index, string url, string title = "T") =>
        new(index, url, url, title, "body", 0, DateTimeOffset.UnixEpoch, 1);

    private static Dictionary<string, ChapterOutput> Output(string name) =>
        new(StringComparer.Ordinal) { ["text"] = new ChapterOutput(name, "hash") };

    // -- identity ------------------------------------------------------------

    [Fact]
    public void State_is_keyed_on_url_not_filename()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Record(Record(1, "https://e.com/a"), Output("001 - T.txt"));

        Assert.True(checkpoint.IsDone("https://e.com/a"));
        Assert.False(checkpoint.IsDone("https://e.com/b"));
    }

    /// <summary>
    /// Bug: the link-set comparison normalises while the resume check compares
    /// raw strings, so a site that starts adding a trailing slash reports
    /// "identical" and then refetches every chapter.
    /// </summary>
    [Theory]
    [InlineData("https://e.com/a", "https://e.com/a/")]
    [InlineData("https://e.com/a/", "https://e.com/a")]
    [InlineData("https://e.com/a", "https://e.com/a#top")]
    [InlineData("https://e.com/a", "https://E.com/a")]
    public void Cosmetic_url_differences_do_not_trigger_a_refetch(string stored, string current)
    {
        var checkpoint = this.New(links: stored);
        checkpoint.Record(Record(1, stored), Output("001 - T.txt"));

        Assert.True(checkpoint.IsDone(current));
        Assert.Equal(TocComparison.Identical, ResumePlanner.Compare([stored], [current]));
    }

    [Fact]
    public void Query_parameters_are_load_bearing_and_kept()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/r?ch=1");
        checkpoint.Record(Record(1, "https://e.com/r?ch=1"), Output("001 - T.txt"));

        Assert.False(checkpoint.IsDone("https://e.com/r?ch=2"));
    }

    // -- fingerprint ---------------------------------------------------------

    [Fact]
    public void Fingerprint_ignores_the_link_set() =>
        Assert.Equal(
            Checkpoint.FingerprintOf("https://e.com/toc", Selectors),
            Checkpoint.FingerprintOf("https://e.com/toc", Selectors));

    [Fact]
    public void Changing_a_selector_changes_the_fingerprint() =>
        Assert.NotEqual(
            Checkpoint.FingerprintOf("https://e.com/toc", Selectors),
            Checkpoint.FingerprintOf("https://e.com/toc", SelectorSet.Create("a.other", "h1", "article")));

    [Fact]
    public void Changing_the_toc_url_changes_the_fingerprint() =>
        Assert.NotEqual(
            Checkpoint.FingerprintOf("https://e.com/toc", Selectors),
            Checkpoint.FingerprintOf("https://e.com/other", Selectors));

    // -- link set comparison -------------------------------------------------

    [Fact]
    public void Identical_link_sets() =>
        Assert.Equal(TocComparison.Identical, ResumePlanner.Compare(["a", "b"], ["a", "b"]));

    [Fact]
    public void Growth_at_the_end_is_accepted() =>
        Assert.Equal(TocComparison.GrewAtEnd, ResumePlanner.Compare(["a", "b"], ["a", "b", "c"]));

    [Fact]
    public void Growth_at_the_start_is_accepted() =>
        Assert.Equal(TocComparison.GrewAtStart, ResumePlanner.Compare(["b", "c"], ["a", "b", "c"]));

    [Fact]
    public void Growth_at_both_ends_is_ambiguous() =>
        Assert.Equal(TocComparison.Diverged, ResumePlanner.Compare(["b"], ["a", "b", "c"]));

    /// <summary>
    /// Removing from the tail leaves every remaining chapter's number where it
    /// was, so it narrows rather than diverges. Removing from anywhere else
    /// shifts the numbering and is still ambiguous.
    /// </summary>
    [Fact]
    public void Removal_from_the_tail_narrows() =>
        Assert.Equal(TocComparison.Narrowed, ResumePlanner.Compare(["a", "b"], ["a"]));

    [Fact]
    public void Removal_from_the_head_diverges() =>
        Assert.Equal(TocComparison.Diverged, ResumePlanner.Compare(["a", "b"], ["b"]));

    [Fact]
    public void Interior_reorder_diverges() =>
        Assert.Equal(TocComparison.Diverged, ResumePlanner.Compare(["a", "b", "c"], ["a", "c", "b"]));

    // -- planning ------------------------------------------------------------

    [Fact]
    public void No_checkpoint_means_no_plan() =>
        Assert.Null(ResumePlanner.Plan(this.directory, "https://e.com/toc", Selectors, ["a"]));

    [Fact]
    public void Resume_is_the_default()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a", "https://e.com/b");
        checkpoint.Record(Record(1, "https://e.com/a"), Output("001 - T.txt"));
        checkpoint.Save();

        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc", Selectors, ["https://e.com/a", "https://e.com/b"]);

        Assert.NotNull(plan);
        Assert.True(plan.Usable);
        Assert.Equal(1, plan.AlreadyDone);
    }

    [Fact]
    public void Force_discards_the_checkpoint()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Record(Record(1, "https://e.com/a"), Output("001 - T.txt"));
        checkpoint.Save();

        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc", Selectors, ["https://e.com/a"], force: true);

        Assert.Null(plan);
        Assert.False(File.Exists(Checkpoint.PathFor(this.directory)));
    }

    [Fact]
    public void Changed_selectors_refuse_to_resume()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Save();

        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc",
            SelectorSet.Create("a.different", "h1", "article"), ["https://e.com/a"]);

        Assert.NotNull(plan);
        Assert.False(plan.Usable);
        Assert.Contains("--link was", plan.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Appended_chapters_are_named()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Save();

        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc", Selectors, ["https://e.com/a", "https://e.com/b"]);

        Assert.Equal(["https://e.com/b"], plan!.Appended);
        Assert.Equal(TocComparison.GrewAtEnd, plan.Comparison);
    }

    [Fact]
    public void Prepend_resumes_and_flags_renumbering()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/b");
        checkpoint.Record(Record(1, "https://e.com/b"), Output("001 - T.txt"));
        checkpoint.Save();

        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc", Selectors, ["https://e.com/a", "https://e.com/b"]);

        Assert.True(plan!.Usable);
        Assert.True(plan.Renumbering);
    }

    [Fact]
    public void Divergent_toc_refuses_with_specifics()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a", "https://e.com/b");
        checkpoint.Save();

        // Not a prefix: the first chapter is gone, so every number after it
        // would shift against the files already written.
        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc", Selectors, ["https://e.com/b"]);

        Assert.False(plan!.Usable);
        Assert.Contains("stored 2 link(s), found 1", plan.Refusal, StringComparison.Ordinal);
    }

    // -- persistence ---------------------------------------------------------

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp_files()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Record(Record(1, "https://e.com/a"), Output("001 - T.txt"));
        checkpoint.Save();

        Assert.True(File.Exists(Checkpoint.PathFor(this.directory)));
        Assert.Empty(Directory.GetFiles(this.directory, ".state-*.tmp"));
    }

    [Fact]
    public void Roundtrip_preserves_every_field()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Record(Record(7, "https://e.com/a", "Seven"), Output("007 - Seven.txt"));
        checkpoint.Save();

        var loaded = Checkpoint.Load(this.directory);

        Assert.NotNull(loaded);
        Assert.Equal(checkpoint.TocUrl, loaded.TocUrl);
        Assert.Equal(checkpoint.Fingerprint, loaded.Fingerprint);
        Assert.Equal(["https://e.com/a"], loaded.LinkSet);

        var entry = loaded.Completed["https://e.com/a"];
        Assert.Equal(7, entry.Index);
        Assert.Equal("Seven", entry.Title);
        Assert.Equal("007 - Seven.txt", entry.Outputs["text"].Name);
        Assert.Equal("hash", entry.Outputs["text"].Sha256);
    }

    [Fact]
    public void Corrupt_checkpoint_is_ignored_not_fatal()
    {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(Checkpoint.PathFor(this.directory), "{not json");
        List<string> warnings = [];

        Assert.Null(Checkpoint.Load(this.directory, warnings.Add));
        Assert.Single(warnings);
    }

    [Fact]
    public void Unknown_schema_version_is_ignored()
    {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(Checkpoint.PathFor(this.directory), """{"version": 99}""");
        List<string> warnings = [];

        Assert.Null(Checkpoint.Load(this.directory, warnings.Add));
        Assert.Contains("schema version 99", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Recording_a_success_clears_an_earlier_failure()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.RecordFailure("https://e.com/a", "timeout", 3);

        checkpoint.Record(Record(1, "https://e.com/a"), Output("001 - T.txt"));

        Assert.Empty(checkpoint.Failed);
    }

    /// <summary>
    /// The checkpoint records what each exporter wrote, so a markdown-only run
    /// has a markdown filename to resume from. Python records the text
    /// exporter's name alone.
    /// </summary>
    [Fact]
    public void Outputs_are_recorded_per_format()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a");
        checkpoint.Record(
            Record(1, "https://e.com/a"),
            new Dictionary<string, ChapterOutput>(StringComparer.Ordinal)
            {
                ["text"] = new ChapterOutput("001 - T.txt", "h1"),
                ["markdown"] = new ChapterOutput("001 - T.md", "h2"),
            });
        checkpoint.Save();

        var prior = Checkpoint.Load(this.directory)!.AsPriorChapters()["https://e.com/a"];

        Assert.Equal("001 - T.txt", prior.Output("text")!.Name);
        Assert.Equal("001 - T.md", prior.Output("markdown")!.Name);
    }

    /// <summary>
    /// Bug: any shrinkage is called divergent and refuses to resume, so asking
    /// for fewer chapters than last time throws away a completed run. A shorter
    /// list that is still a prefix leaves every remaining chapter's number
    /// exactly where it was.
    /// </summary>
    [Fact]
    public void Lowering_the_chapter_cap_still_resumes()
    {
        var checkpoint = this.New("https://e.com/toc", "https://e.com/a", "https://e.com/b", "https://e.com/c");
        checkpoint.Record(Record(1, "https://e.com/a"), Output("001 - T.txt"));
        checkpoint.Record(Record(2, "https://e.com/b"), Output("002 - T.txt"));
        checkpoint.Save();

        var plan = ResumePlanner.Plan(
            this.directory, "https://e.com/toc", Selectors, ["https://e.com/a", "https://e.com/b"]);

        Assert.NotNull(plan);
        Assert.True(plan.Usable);
        Assert.Equal(TocComparison.Narrowed, plan.Comparison);
        Assert.False(plan.Renumbering);
    }

    [Fact]
    public void Narrowing_is_a_prefix_not_any_shrinkage()
    {
        Assert.Equal(TocComparison.Narrowed, ResumePlanner.Compare(["a", "b", "c"], ["a", "b"]));
        Assert.Equal(TocComparison.Diverged, ResumePlanner.Compare(["a", "b", "c"], ["b", "c"]));
        Assert.Equal(TocComparison.Diverged, ResumePlanner.Compare(["a", "b", "c"], ["a", "c"]));
    }
}

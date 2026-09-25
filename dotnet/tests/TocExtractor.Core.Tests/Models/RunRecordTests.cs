using System.Text;
using TocExtractor.Core.Links;
using TocExtractor.Core.Models;

namespace TocExtractor.Core.Tests.Models;

public sealed class RunRecordTests
{
    private static ChapterRecord Record(int index = 1, string text = "body", string? finalUrl = null) =>
        new(
            Index: index,
            RequestedUrl: "https://e.com/ch/1",
            FinalUrl: finalUrl ?? "https://e.com/ch/1",
            Title: "One",
            Text: text,
            StrippedUrls: 0,
            FetchedAt: DateTimeOffset.UnixEpoch,
            Attempts: 1);

    [Fact]
    public void Byte_count_is_utf8_not_characters()
    {
        var record = Record(text: "章章");

        Assert.Equal(6, record.ByteCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(record.Text), record.ByteCount);
    }

    [Fact]
    public void Sha256_is_lowercase_hex_of_the_utf8_text()
    {
        // Known vector: the SHA-256 of the empty string.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Record(text: "").Sha256);
    }

    [Fact]
    public void Redirected_compares_requested_against_final()
    {
        Assert.False(Record().Redirected);
        Assert.True(Record(finalUrl: "https://e.com/ch/1/").Redirected);
    }

    [Fact]
    public void Attempted_counts_successes_and_failures()
    {
        var result = new RunResult(
            "https://e.com/toc",
            new LinkTally(2, ["https://e.com/a", "https://e.com/b"]),
            Completed: [Record()],
            Failed: [new FailedChapter(2, "https://e.com/b", "timeout", "…", 3)]);

        Assert.Equal(2, result.Attempted);
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public void Total_stripped_urls_sums_across_chapters()
    {
        var result = new RunResult(
            "https://e.com/toc",
            new LinkTally(2, ["https://e.com/a", "https://e.com/b"]),
            Completed:
            [
                Record() with { StrippedUrls = 2 },
                Record(index: 2) with { StrippedUrls = 3 },
            ]);

        Assert.Equal(5, result.TotalStrippedUrls);
    }

    /// <summary>
    /// A kept link that produced neither a record nor a failure has been lost.
    /// The check is the backstop for the same class of bug the tally's
    /// constructor catches one layer earlier.
    /// </summary>
    [Fact]
    public void Accounting_notices_a_kept_link_that_produced_nothing()
    {
        var result = new RunResult(
            "https://e.com/toc",
            new LinkTally(2, ["https://e.com/a", "https://e.com/b"]),
            Completed: [Record()]);

        Assert.False(result.AccountsForEveryLink());
    }

    [Fact]
    public void Resumed_skips_count_towards_the_accounting()
    {
        var result = new RunResult(
            "https://e.com/toc",
            new LinkTally(2, ["https://e.com/a", "https://e.com/b"]),
            Completed: [Record()],
            SkippedResumed: ["https://e.com/b"]);

        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public void Empty_run_collections_default_to_empty_not_null()
    {
        var result = new RunResult("https://e.com/toc", new LinkTally(0));

        Assert.Empty(result.Completed);
        Assert.Empty(result.Failed);
        Assert.Empty(result.SkippedResumed);
        Assert.Empty(result.AppendedLinks);
        Assert.True(result.AccountsForEveryLink());
    }

    [Fact]
    public void Robots_decision_travels_with_the_record()
    {
        var record = Record() with
        {
            Robots = new RobotsDecision(Allowed: true, "rule", AuthenticatedOverride: true),
        };

        var entry = record.Robots!.Value.AsManifestEntry();

        Assert.Equal(true, entry["robots_allowed"]);
        Assert.Equal("rule", entry["robots_rule"]);
        Assert.Equal(true, entry["robots_authenticated_override"]);
    }
}

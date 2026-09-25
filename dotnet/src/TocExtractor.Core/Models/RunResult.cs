using TocExtractor.Core.Links;

namespace TocExtractor.Core.Models;

/// <summary>End-of-run accounting.</summary>
/// <remarks>
/// <see cref="Collection"/> carries the raw/kept/rejected/truncated invariant,
/// so a caller can report what happened to every link the table of contents
/// offered rather than only what succeeded.
/// </remarks>
public sealed record RunResult(
    string TocUrl,
    LinkTally Collection,
    IReadOnlyList<ChapterRecord>? Completed = null,
    IReadOnlyList<FailedChapter>? Failed = null,
    IReadOnlyList<string>? SkippedResumed = null,
    IReadOnlyList<string>? AppendedLinks = null)
{
    public IReadOnlyList<ChapterRecord> Completed { get; init; } = Completed ?? [];

    public IReadOnlyList<FailedChapter> Failed { get; init; } = Failed ?? [];

    public IReadOnlyList<string> SkippedResumed { get; init; } = SkippedResumed ?? [];

    public IReadOnlyList<string> AppendedLinks { get; init; } = AppendedLinks ?? [];

    public int Attempted => this.Completed.Count + this.Failed.Count;

    public int TotalStrippedUrls => this.Completed.Sum(record => record.StrippedUrls);

    /// <summary>kept == completed + failed + resumed-skips.</summary>
    public bool AccountsForEveryLink() =>
        this.Collection.Kept.Count
        == this.Completed.Count + this.Failed.Count + this.SkippedResumed.Count;
}

namespace TocExtractor.Core.Models;

/// <summary>One chapter that could not be extracted, and why.</summary>
public sealed record FailedChapter(int Index, string Url, string Reason, string Detail, int Attempts);

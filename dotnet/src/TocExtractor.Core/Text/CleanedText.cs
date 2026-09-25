namespace TocExtractor.Core.Text;

/// <summary>Cleaned body text, and how many URLs cleaning removed.</summary>
/// <remarks>
/// The count is returned as a peer of the text rather than logged, so a caller
/// cannot report the text without also having the loss in hand.
/// </remarks>
/// <param name="Text">The cleaned text.</param>
/// <param name="StrippedUrls">How many URLs were deleted from it.</param>
public readonly record struct CleanedText(string Text, int StrippedUrls);

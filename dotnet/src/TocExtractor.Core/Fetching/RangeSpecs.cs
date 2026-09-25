namespace TocExtractor.Core.Fetching;

/// <summary>A chapter whose address is known, with the number it is saved under.</summary>
public sealed record NumberedLink(int Number, string Url);

/// <summary>
/// One walk along a book's next or previous links, saving the chapters it
/// passes that fall inside the range.
/// </summary>
/// <param name="StartUrl">A chapter whose address is known, where the walk begins.</param>
/// <param name="StartNumber">That chapter's number.</param>
/// <param name="StepSelector">The link to follow on each page: next or previous.</param>
/// <param name="Direction">+1 when following next links, -1 when following previous ones.</param>
/// <param name="SaveFrom">The lowest chapter number to save.</param>
/// <param name="SaveTo">The highest chapter number to save.</param>
/// <param name="MaxSteps">A ceiling on pages visited, against a site whose links go in circles.</param>
public sealed record WalkSpec(
    string StartUrl,
    int StartNumber,
    string StepSelector,
    int Direction,
    int SaveFrom,
    int SaveTo,
    int MaxSteps)
{
    public bool Saves(int number) => number >= this.SaveFrom && number <= this.SaveTo;

    /// <summary>Whether a walk at <paramref name="number"/> has gone beyond the far end of its range.</summary>
    public bool IsPast(int number) => this.Direction > 0 ? number >= this.SaveTo : number <= this.SaveFrom;
}

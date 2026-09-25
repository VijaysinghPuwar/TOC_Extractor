namespace TocExtractor.Core.Text;

/// <summary>One allocated path component, and how it was reached.</summary>
/// <param name="Name">The component to write to disk.</param>
/// <param name="CollidedWith">
/// The name this one would have overwritten, or null when there was no clash.
/// </param>
public readonly record struct AllocatedName(string Name, string? CollidedWith = null)
{
    public bool Deduplicated => CollidedWith is not null;
}

namespace TocExtractor.Core.Pages;

/// <summary>A page source a person can use: one that can wait for them to pass a site's check.</summary>
public interface IHumanGate
{
    /// <summary>
    /// Show the person the page asking them to prove they are human, and wait
    /// until they have.
    /// </summary>
    /// <returns>True once no open page shows a check; false if <paramref name="timeout"/> passed first.</returns>
    Task<bool> WaitForPersonAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

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

    /// <summary>Wait until the person has signed in to the site in the browser.</summary>
    /// <returns>True once the browser carries an account cookie; false if <paramref name="timeout"/> passed first.</returns>
    Task<bool> WaitForSignInAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Bring a page showing a check to the front, for a person who could not find it. False if none is showing.</summary>
    Task<bool> ShowCheckAsync() => Task.FromResult(false);

    /// <summary>
    /// As <see cref="WaitForPersonAsync(TimeSpan, CancellationToken)"/>, for one
    /// site among several open: only that site's check can end the wait early
    /// by saying it cannot finish.
    /// </summary>
    Task<bool> WaitForPersonAsync(string? siteUrl, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        this.WaitForPersonAsync(timeout, cancellationToken);

    /// <summary>As <see cref="WaitForSignInAsync(TimeSpan, CancellationToken)"/>, for one site among several open.</summary>
    Task<bool> WaitForSignInAsync(string? siteUrl, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        this.WaitForSignInAsync(timeout, cancellationToken);
}

using System.Reflection;
using System.Runtime.CompilerServices;
using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Tests;

/// <summary>
/// Core carries the fetch loop and every policy decision, and has to stay
/// buildable and testable without a browser — the same boundary pagesource.py
/// draws on the Python side, where CI asserts nothing downstream of the
/// protocol imports Playwright.
/// </summary>
/// <remarks>
/// Written before TocExtractor.Browser exists. A boundary added after the
/// first violation is one that has already failed once, and a driver reference
/// is the kind that arrives by autocomplete rather than by decision.
/// </remarks>
public sealed class AssemblyBoundaryTests
{
    private static readonly string[] BrowserDrivers =
        ["Microsoft.Playwright", "Selenium", "PuppeteerSharp"];

    private static Assembly Core => typeof(RejectionReason).Assembly;

    [Fact]
    public void Core_does_not_reference_a_browser_driver()
    {
        string[] offending = [.. Core.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .Where(name => BrowserDrivers.Any(
                driver => name.StartsWith(driver, StringComparison.Ordinal)))];

        Assert.Empty(offending);
    }

    /// <summary>
    /// Core is a library. An entry point in it means the CLI has started
    /// growing into the layer the tests drive directly.
    /// </summary>
    [Fact]
    public void Core_is_a_library_with_no_entry_point() => Assert.Null(Core.EntryPoint);

    [Fact]
    public void Tests_can_see_internals()
    {
        string[] visible = [.. Core.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)];

        Assert.Contains("TocExtractor.Core.Tests", visible);
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TocExtractor.App;

/// <summary>One step of the first-run browser download.</summary>
public sealed record InstallProgress(string Message, double? Percent = null);

/// <summary>
/// Downloads Chromium on first launch, into the app's own folder.
/// </summary>
/// <remarks>
/// <para>
/// Bundling Chromium would make every download of the app 150 MB larger and
/// tie each release to one browser build. Instead the app ships Playwright's
/// driver, and the driver fetches the browser build that exactly matches it,
/// once, the first time the app opens.
/// </para>
/// <para>
/// The driver is run directly rather than through Playwright's
/// <c>Program.Main</c>, because that writes its progress to a console a
/// windowed app does not have. Reading the output here is what lets the
/// window show a real progress bar instead of a spinner that might be stuck.
/// </para>
/// </remarks>
public static partial class BrowserInstaller
{
    private const string BrowsersVariable = "PLAYWRIGHT_BROWSERS_PATH";
    private const string DriverVariable = "PLAYWRIGHT_DRIVER_SEARCH_PATH";

    /// <summary>The folder holding Playwright's <c>.playwright</c> driver directory.</summary>
    public static string DriverRoot =>
        Environment.GetEnvironmentVariable(DriverVariable) is { Length: > 0 } configured
            ? configured
            : AppContext.BaseDirectory;

    /// <summary>
    /// Inside a macOS app bundle the driver lives in Contents/Resources, not
    /// beside the executable: Contents/MacOS may hold only code, and a folder
    /// of scripts there makes codesign refuse the whole bundle. Point
    /// Playwright at it when that is the layout. Elsewhere this does nothing.
    /// </summary>
    public static void UseBundledDriver(string? baseDirectory = null)
    {
        var here = baseDirectory ?? AppContext.BaseDirectory;
        if (Directory.Exists(Path.Combine(here, ".playwright"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(DriverVariable)))
        {
            return;
        }

        var resources = Path.GetFullPath(Path.Combine(here, "..", "Resources"));
        if (Directory.Exists(Path.Combine(resources, ".playwright")))
        {
            Environment.SetEnvironmentVariable(DriverVariable, resources);
        }
    }

    /// <summary>
    /// Point Playwright at the app's browser folder. Call once at startup,
    /// before anything touches Playwright. An explicit setting is respected.
    /// </summary>
    public static void UseAppBrowsers(string? directory = null)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(BrowsersVariable)))
        {
            Environment.SetEnvironmentVariable(BrowsersVariable, directory ?? AppPaths.Browsers);
        }
    }

    /// <summary>Whether every browser the driver needs is already downloaded and complete.</summary>
    public static async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        var (exitCode, output) = await RunDriverAsync(["install", "--dry-run", "--no-shell", "chromium"], null, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            return false;
        }

        var locations = InstallLocations(output);
        return locations.Count > 0
            && locations.All(location => File.Exists(Path.Combine(location, "INSTALLATION_COMPLETE")));
    }

    /// <summary>Download Chromium, reporting each step. Throws with the driver's own words on failure.</summary>
    public static async Task InstallAsync(IProgress<InstallProgress>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Report(new InstallProgress("Preparing the download"));
        var (exitCode, output) = await RunDriverAsync(
            ["install", "--no-shell", "chromium"],
            line =>
            {
                if (ParseLine(line) is { } step)
                {
                    progress?.Report(step);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var tail = string.Join(Environment.NewLine, output.TakeLast(6));
            throw new InvalidOperationException(
                $"The browser download failed (exit code {exitCode}). Check the internet connection and try again."
                + Environment.NewLine + tail);
        }

        progress?.Report(new InstallProgress("Browser ready", 100));
    }

    /// <summary>Turn one line of driver output into a progress step, or null for noise.</summary>
    internal static InstallProgress? ParseLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var percent = PercentPattern().Match(text);
        if (percent.Success)
        {
            var value = double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture);
            var size = SizePattern().Match(text);
            return new InstallProgress(
                size.Success ? $"Downloading ({size.Groups[1].Value})" : "Downloading",
                Math.Clamp(value, 0, 100));
        }

        if (text.StartsWith("Downloading ", StringComparison.Ordinal))
        {
            var name = text["Downloading ".Length..];
            var from = name.IndexOf(" from ", StringComparison.Ordinal);
            return new InstallProgress("Downloading " + (from > 0 ? name[..from] : name), 0);
        }

        return text.Contains("downloaded to", StringComparison.Ordinal)
            ? new InstallProgress("Unpacking", 100)
            : null;
    }

    internal static IReadOnlyList<string> InstallLocations(IEnumerable<string> dryRunOutput) =>
        [.. dryRunOutput
            .Select(line => LocationPattern().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value.Trim())];

    /// <summary>Playwright's bundled node and CLI, laid out beside the app by the build.</summary>
    public static (string Node, string Cli) FindDriver(string baseDirectory)
    {
        var root = Path.Combine(baseDirectory, ".playwright");
        var cli = Path.Combine(root, "package", "cli.js");
        var nodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var node = Directory.Exists(Path.Combine(root, "node"))
            ? Directory.GetDirectories(Path.Combine(root, "node"))
                .Select(platform => Path.Combine(platform, nodeName))
                .FirstOrDefault(File.Exists)
            : null;

        if (node is null || !File.Exists(cli))
        {
            throw new FileNotFoundException(
                $"The Playwright driver is missing from {root}. The app was not built completely; download it again.");
        }

        return (node, cli);
    }

    private static async Task<(int ExitCode, List<string> Output)> RunDriverAsync(
        string[] arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var (node, cli) = FindDriver(DriverRoot);
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        List<string> output = [];
        var gate = new Lock();
        void Collect(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                output.Add(line);
            }

            onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        lock (gate)
        {
            return (process.ExitCode, [.. output]);
        }
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentPattern();

    [GeneratedRegex(@"of\s+([\d.]+\s*[KMG]i?B)")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"Install location:\s+(.+)$")]
    private static partial Regex LocationPattern();
}

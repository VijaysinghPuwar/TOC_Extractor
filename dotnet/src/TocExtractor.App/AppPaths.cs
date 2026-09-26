namespace TocExtractor.App;

/// <summary>Where the desktop app keeps things on this machine.</summary>
/// <remarks>
/// A double-clicked app's working directory is <c>/</c> on macOS and wherever
/// Windows chose, often read-only and never somewhere a person would look. So
/// nothing here is relative: state goes in the per-user application folder
/// (~/Library/Application Support on macOS, %APPDATA% on Windows) and output
/// in Downloads, where people expect files an app saved for them.
/// </remarks>
public static class AppPaths
{
    public const string AppName = "TOC Extractor";

    /// <summary>The per-user application folder.</summary>
    public static string DataDirectory => DataDirectoryFor(OperatingSystem.IsMacOS());

    /// <summary>
    /// Where the browser and its profile live: the data folder, except on
    /// Windows, where they belong to this machine rather than the person.
    /// </summary>
    /// <remarks>
    /// %APPDATA% is the roaming folder: on a work network it is copied to a
    /// server at every sign-out, and backup and sync tools treat it as the
    /// person's documents. Chromium is about 430 MB and its profile is a cache
    /// written with every page, so Windows keeps them in %LOCALAPPDATA%, as
    /// Playwright itself does.
    /// </remarks>
    public static string MachineDirectory => MachineDirectoryFor(OperatingSystem.IsWindows());

    /// <summary>The persistent browser profile, which keeps a sign-in between runs.</summary>
    public static string BrowserProfile => Path.Combine(MachineDirectory, "browser-profile");

    /// <summary>Where Chromium is downloaded on first launch.</summary>
    public static string Browsers => Path.Combine(MachineDirectory, "browsers");

    /// <summary>Saved site profiles, in the TOML the command line reads.</summary>
    public static string Profiles => Path.Combine(DataDirectory, "profiles");

    /// <summary>The detailed CSV logs: one per extraction, plus the app's own.</summary>
    public static string Logs => Path.Combine(DataDirectory, "logs");

    /// <summary>What the app has learned about sites that ask for checks.</summary>
    public static string SitePaces => Path.Combine(DataDirectory, "site-paces.json");

    /// <summary>The window's last-used settings.</summary>
    public static string LastSession => Path.Combine(DataDirectory, "last-session.json");

    public static string DefaultOutputDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", AppName);

    internal static string DataDirectoryFor(bool macOs)
    {
        if (macOs)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                AppName);
        }

        // ApplicationData is %APPDATA% on Windows and $XDG_CONFIG_HOME (or
        // ~/.config) on Linux, which is where each platform expects it.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    }

    internal static string MachineDirectoryFor(bool windows) =>
        windows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName)
            : DataDirectory;

    /// <summary>
    /// Moves a browser and profile an earlier version kept in the roaming
    /// folder to where this one looks, so an update keeps the sign-in and
    /// does not download Chromium again. Does nothing once moved, and nothing
    /// off Windows.
    /// </summary>
    public static void MoveMachineFilesOutOfRoaming() =>
        MoveMachineFiles(DataDirectory, MachineDirectory);

    internal static void MoveMachineFiles(string from, string to)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var name in new[] { "browsers", "browser-profile" })
        {
            var old = Path.Combine(from, name);
            var now = Path.Combine(to, name);
            if (!Directory.Exists(old) || Directory.Exists(now))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(to);
                Directory.Move(old, now);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still in use, or on another drive: the app downloads a fresh
                // browser, and the old folder is left for the person.
            }
        }
    }
}

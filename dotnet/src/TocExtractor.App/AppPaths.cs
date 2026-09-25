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

    /// <summary>The persistent browser profile, which keeps a sign-in between runs.</summary>
    public static string BrowserProfile => Path.Combine(DataDirectory, "browser-profile");

    /// <summary>Where Chromium is downloaded on first launch.</summary>
    public static string Browsers => Path.Combine(DataDirectory, "browsers");

    /// <summary>Saved site profiles, in the TOML the command line reads.</summary>
    public static string Profiles => Path.Combine(DataDirectory, "profiles");

    /// <summary>The detailed CSV logs: one per extraction, plus the app's own.</summary>
    public static string Logs => Path.Combine(DataDirectory, "logs");

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
}

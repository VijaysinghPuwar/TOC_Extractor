using TocExtractor.App.Pipeline;
using TocExtractor.App.Session;

namespace TocExtractor.App.Tests;

public sealed class WindowsTests
{
    [Theory]
    [InlineData("Wait...", "Wait")]
    [InlineData("Trailing space ", "Trailing space")]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("com1.part", "_com1.part")]
    [InlineData("Console", "Console")]
    [InlineData("A Plain Title", "A Plain Title")]
    public void Book_folder_names_are_ones_Windows_creates_as_named(string name, string expected)
    {
        Assert.Equal(expected, BookFiles.WindowsSafe(name));
    }

    [Theory]
    [InlineData("Failed to create a ProcessSingleton for your profile directory.")]
    [InlineData("Browser closed.\n[pid=1][out] Opening in existing browser session.")]
    public void A_browser_still_holding_the_profile_is_recognised_on_every_system(string message)
    {
        Assert.True(BrowserHost.EarlierBrowserStillOpen(message));
    }

    [Fact]
    public void Another_launch_failure_is_not_taken_for_a_browser_still_open()
    {
        Assert.False(BrowserHost.EarlierBrowserStillOpen("Executable doesn't exist at chrome.exe"));
    }

    [Fact]
    public void Off_Windows_the_browser_stays_in_the_data_folder()
    {
        Assert.Equal(AppPaths.DataDirectory, AppPaths.MachineDirectoryFor(windows: false));
    }

    [Fact]
    public void On_Windows_the_browser_is_kept_in_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.Combine(local, AppPaths.AppName), AppPaths.MachineDirectoryFor(windows: true));
    }

    [Fact]
    public void An_earlier_browser_and_profile_are_moved_once_and_never_overwritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "toc-move-" + Guid.NewGuid().ToString("N"));
        var from = Path.Combine(root, "roaming");
        var to = Path.Combine(root, "local");
        try
        {
            Directory.CreateDirectory(Path.Combine(from, "browsers", "chromium-1"));
            Directory.CreateDirectory(Path.Combine(from, "browser-profile"));
            File.WriteAllText(Path.Combine(from, "browser-profile", "Cookies"), "signed in");
            Directory.CreateDirectory(Path.Combine(to, "browsers"));

            AppPaths.MoveMachineFiles(from, to);

            // The profile moves, keeping the sign-in.
            Assert.Equal("signed in", File.ReadAllText(Path.Combine(to, "browser-profile", "Cookies")));
            Assert.False(Directory.Exists(Path.Combine(from, "browser-profile")));

            // A browser already in the new place is kept, and the old one left alone.
            Assert.False(Directory.Exists(Path.Combine(to, "browsers", "chromium-1")));
            Assert.True(Directory.Exists(Path.Combine(from, "browsers", "chromium-1")));

            // Settings and logs are not the browser's, and stay where they were.
            AppPaths.MoveMachineFiles(from, to);
            Assert.True(Directory.Exists(Path.Combine(from, "browsers")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Nothing_moves_when_both_places_are_the_same()
    {
        var root = Path.Combine(Path.GetTempPath(), "toc-same-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "browsers"));
            AppPaths.MoveMachineFiles(root, root);
            Assert.True(Directory.Exists(Path.Combine(root, "browsers")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

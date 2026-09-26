namespace TocExtractor.Browser.Tests;

/// <summary>How much of the machine the browser may use. No browser needed.</summary>
public sealed class MachineBudgetTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(8, 8, 8)] // a base MacBook Air: a handful of tabs, not 48
    [InlineData(16, 10, 24)]
    [InlineData(24, 10, 40)]
    [InlineData(64, 16, 48)] // a large Mac keeps the full 48
    [InlineData(4, 4, MachineBudget.FewestTabs)] // never fewer than a few
    [InlineData(128, 4, 16)] // plenty of memory, few cores: the cores decide
    public void The_tab_ceiling_follows_the_machine(long gigabytes, int cores, int tabs) =>
        Assert.Equal(tabs, MachineBudget.MostTabsFor(gigabytes * Gigabyte, cores));

    [Fact]
    public void Unknown_memory_goes_by_the_cores_and_never_above_the_old_ceiling()
    {
        Assert.Equal(40, MachineBudget.MostTabsFor(0, 10));
        Assert.Equal(MachineBudget.MostTabsAnywhere, MachineBudget.MostTabsFor(0, 64));
    }

    [Fact]
    public void This_machine_is_read()
    {
        Assert.True(MachineBudget.Cores >= 1);
        Assert.InRange(MachineBudget.MostTabs, MachineBudget.FewestTabs, MachineBudget.MostTabsAnywhere);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            Assert.True(MachineBudget.MemoryBytes > Gigabyte);
            Assert.InRange(MachineBudget.FreeMemoryPercent() ?? -1, 0, 100);
        }

        Assert.Contains("browser tabs", MachineBudget.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_is_tight_only_when_the_system_says_so()
    {
        var real = MachineBudget.FreePercentReader;
        try
        {
            MachineBudget.FreePercentReader = () => 5;
            Assert.True(MachineBudget.MemoryTight());
            MachineBudget.FreePercentReader = () => 60;
            Assert.False(MachineBudget.MemoryTight());

            // A system that does not say is never counted as short.
            MachineBudget.FreePercentReader = () => null;
            Assert.False(MachineBudget.MemoryTight());
            MachineBudget.FreePercentReader = () => throw new IOException("unreadable");
            Assert.False(MachineBudget.MemoryTight());
        }
        finally
        {
            MachineBudget.FreePercentReader = real;
        }
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(24, 1)]
    [InlineData(40, 2)]
    [InlineData(48, 3)]
    public void Small_machines_print_one_pdf_at_a_time(int mostTabs, int atOnce) =>
        Assert.Equal(atOnce, PdfBook.AtOnce(mostTabs));
}

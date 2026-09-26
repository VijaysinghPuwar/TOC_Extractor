using System.CommandLine;

namespace TocExtractor.Cli.Tests;

public sealed class CommandLineTests
{
    /// <summary>
    /// Regression: the entry point read the required --toc value before the
    /// built-in help action could run, so `--help` exited with an unhandled
    /// exception and a stack trace instead of printing the options. It shipped
    /// in a published binary before anyone ran it.
    /// </summary>
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    public void Built_in_actions_run_without_the_required_options(string flag)
    {
        var parsed = CommandLine.Build().Parse(flag);

        Assert.Empty(parsed.Errors);
        Assert.NotNull(parsed.Action);
    }

    [Fact]
    public void Help_lists_every_option_the_readme_documents()
    {
        var writer = new StringWriter();
        var parsed = CommandLine.Build().Parse("--help");

        var exit = parsed.Invoke(new InvocationConfiguration { Output = writer });
        var help = writer.ToString();

        Assert.Equal(0, exit);
        foreach (var option in CommandLine.All)
        {
            Assert.Contains(option.Name, help, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_missing_required_option_is_a_parse_error_not_a_crash()
    {
        var parsed = CommandLine.Build().Parse("--link a.ch");

        Assert.NotEmpty(parsed.Errors);
        Assert.Contains(parsed.Errors, e => e.Message.Contains("--toc", StringComparison.Ordinal));
    }

    [Fact]
    public void Version_reports_the_same_string_the_python_package_does()
    {
        var writer = new StringWriter();

        CommandLine.Build().Parse("--version").Invoke(new InvocationConfiguration { Output = writer });

        Assert.Equal("2.2.0", writer.ToString().Trim());
    }
}

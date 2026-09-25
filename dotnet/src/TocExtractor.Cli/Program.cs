
namespace TocExtractor.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = CommandLine.Build();
        var parsed = root.Parse(args);

        if (parsed.Errors.Count > 0 || parsed.GetValue(CommandLine.Toc) is null)
        {
            foreach (var error in parsed.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return parsed.Errors.Count > 0 ? CommandLine.ExitUsage : await parsed.InvokeAsync();
        }

        try
        {
            var settings = RunSettings.From(parsed, args);
            return await Extraction.RunAsync(settings);
        }
        catch (ProfileException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return CommandLine.ExitUsage;
        }
    }
}

using System.CommandLine;

namespace TocExtractor.Cli;

internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        var root = CommandLine.Build();

        // The action is set rather than the parse result being read directly.
        // Help, version and parse errors are all built-in actions, and reading
        // a required option's value before they run throws instead of printing
        // them — which made `--help` exit with a stack trace.
        root.SetAction((parseResult, cancellationToken) =>
            RunAsync(parseResult, args, cancellationToken));

        return root.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunAsync(
        ParseResult parsed,
        string[] args,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Extraction.RunAsync(
                RunSettings.From(parsed, args), cancellationToken: cancellationToken);
        }
        catch (ProfileException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return CommandLine.ExitUsage;
        }
        catch (OperationCanceledException)
        {
            // Progress is already on disk: the checkpoint is written after
            // every chapter, not at the end.
            Console.Error.WriteLine("interrupted; rerun the same command to resume");
            return CommandLine.ExitFailed;
        }
    }
}

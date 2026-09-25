using System.Collections.Concurrent;
using System.Net;
using TocExtractor.App.Pipeline;
using TocExtractor.App.Session;
using TocExtractor.Core.Models;
using TocExtractor.Core.Politeness;
using TocExtractor.Core.Tests.Fetching;

namespace TocExtractor.App.Tests;

/// <summary>Every host is public, so no test touches DNS.</summary>
internal sealed class PublicResolver : IHostResolver
{
    public IReadOnlyList<IPAddress> Resolve(string host) => [IPAddress.Parse("93.184.216.34")];
}

/// <summary>Records everything a pipeline reports.</summary>
internal sealed class RecordingObserver : IPipelineObserver
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ConcurrentQueue<ChapterRecord> Records { get; } = new();

    public ConcurrentQueue<FailedChapter> Failures { get; } = new();

    public IReadOnlyList<string> Resumed { get; private set; } = [];

    public void Log(string line) => this.Lines.Enqueue(line);

    public void Record(ChapterRecord record) => this.Records.Enqueue(record);

    public void Failure(FailedChapter failure) => this.Failures.Enqueue(failure);

    public void Resuming(Core.Checkpoints.ResumePlan plan, IReadOnlyList<string> alreadyDone) =>
        this.Resumed = alreadyDone;
}

/// <summary>A small book and a session wired to it.</summary>
internal static class Book
{
    public const string Toc = "https://e.com/toc";

    public static Dictionary<string, StubPage> Pages(int chapters)
    {
        Dictionary<string, StubPage> pages = new(StringComparer.Ordinal)
        {
            [Toc] = new StubPage
            {
                Links = [.. Enumerable.Range(1, chapters).Select(i => (object?)$"https://e.com/ch/{i}")],
            },
        };

        foreach (var i in Enumerable.Range(1, chapters))
        {
            pages[$"https://e.com/ch/{i}"] = new StubPage
            {
                Title = $"Chapter {i}",
                Body = $"The body of chapter {i}, with a few words in it.",
            };
        }

        return pages;
    }

    public static SessionSettings Settings(string output) => new()
    {
        TocUrl = Toc,
        LinkSelector = "a.ch",
        TitleSelector = "h1",
        ContentSelector = "article",
        OutputDirectory = output,
        MaxChapters = 50,
        Concurrency = 1,
        MinDelaySeconds = 0,
        MaxDelaySeconds = 0,
    };

    public static (ExtractionSession Session, StubPageSource Source) Session(
        IReadOnlyDictionary<string, StubPage> pages,
        string? robots = null,
        bool authenticated = false,
        string? profileDirectory = null)
    {
        var source = new StubPageSource(pages, authenticated: authenticated, maxConcurrent: 3);
        var environment = new SessionEnvironment
        {
            StartSource = (_, _, _) => Task.FromResult<Core.Pages.IPageSource>(source),
            FetchRobots = (_, _) => robots,
            BrowserProfileDirectory = profileDirectory ?? Scratch.Directory(),
            Resolver = new PublicResolver(),
        };
        return (new ExtractionSession(environment), source);
    }
}

internal static class Scratch
{
    public static string Directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "toc-extractor-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}

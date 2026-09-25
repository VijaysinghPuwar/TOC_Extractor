using TocExtractor.App.Pipeline;
using TocExtractor.App.Scanning;

namespace TocExtractor.App.Session;

/// <summary>What one extraction in the desktop window asks of its session. <see cref="NovelSession"/> in the app, a fake in tests.</summary>
public interface INovelService : IAsyncDisposable
{
    /// <summary>Raised with what the person must do in the browser, then with null when it is done.</summary>
    event EventHandler<string?>? PersonNeeded;

    bool SignedIn { get; }

    SessionSettings Pace { get; set; }

    Task<ScanResult> ScanAsync(string novelUrl, Action<string>? log, CancellationToken cancellationToken = default);

    RangePreview Preview(ScanResult scan, int first, int last);

    Task<RangeResult> SaveAsync(
        ScanResult scan,
        RangePreview preview,
        string outputRoot,
        bool text,
        bool pdf,
        bool force,
        IPipelineObserver observer,
        CancellationToken cancellationToken = default);

    Task BeginSignInAsync(string novelUrl, CancellationToken cancellationToken = default);

    Task<bool> FinishSignInAsync(CancellationToken cancellationToken = default);

    Task CloseAsync();
}

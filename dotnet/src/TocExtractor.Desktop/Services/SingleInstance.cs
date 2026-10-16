using System.Security.Cryptography;
using System.Text;

namespace TocExtractor.Desktop.Services;

/// <summary>One copy of the app at a time on Windows; opening it again shows the one already open.</summary>
/// <remarks>
/// <para>
/// Every extraction shares one browser, with one profile, and Chromium lets
/// only one process use a profile. A second copy of the app, opened from the
/// Start menu while the first sat minimised, could scan nothing: "The browser
/// from an earlier run is still open." Seen on a real machine, with two
/// copies open at once. So the second copy wakes the first and closes.
/// </para>
/// <para>
/// Named after the data folder, so a copy run with its own TOC_EXTRACTOR_HOME
/// (an end-to-end test beside the person's own) is a separate app. macOS
/// keeps one copy of an app itself, and named events exist only on Windows.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex running;
    private readonly EventWaitHandle wake;
    private readonly CancellationTokenSource stop = new();

    private SingleInstance(Mutex running, EventWaitHandle wake)
    {
        this.running = running;
        this.wake = wake;
    }

    /// <summary>
    /// This copy's claim to be the one running, or null when another copy
    /// already is: that copy has then been asked to come to the front.
    /// </summary>
    public static SingleInstance? Claim(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var name = Name(dataDirectory);
        var wake = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        var running = new Mutex(initiallyOwned: true, name + "-running", out var first);
        if (first)
        {
            return new SingleInstance(running, wake);
        }

        wake.Set();
        wake.Dispose();
        running.Dispose();
        throw new AlreadyRunningException();
    }

    /// <summary>Call <paramref name="show"/> each time another copy is opened.</summary>
    public void Listen(Action show)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { this.wake, this.stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                show();
            }
        })
        {
            IsBackground = true,
            Name = "Show when opened again",
        };
        thread.Start();
    }

    public void Dispose()
    {
        this.stop.Cancel();
        try
        {
            this.running.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released already, or from another thread: closing frees it anyway.
        }

        this.running.Dispose();
        this.wake.Dispose();
        this.stop.Dispose();
    }

    internal static string Name(string dataDirectory)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        return @"Local\TocExtractor-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
    }
}

/// <summary>Another copy of the app is open, and has been brought to the front.</summary>
internal sealed class AlreadyRunningException : Exception
{
    public AlreadyRunningException()
        : base("TOC Extractor is already open.")
    {
    }

    public AlreadyRunningException(string message)
        : base(message)
    {
    }

    public AlreadyRunningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

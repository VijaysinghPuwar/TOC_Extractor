using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Pages;

/// <summary>Anything that went wrong loading a page.</summary>
/// <remarks>
/// Named for the .NET convention rather than Python's <c>PageError</c>. The
/// hierarchy is the fetch loop's whole retry vocabulary: everything a page
/// source can throw is translated into one of these before it leaves, so the
/// retry rules are written against this set alone and nothing else can reach
/// them.
/// </remarks>
public class PageException : Exception
{
    public PageException()
    {
    }

    public PageException(string message)
        : base(message)
    {
    }

    public PageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The page did not load, or a selector did not resolve, in time.</summary>
/// <remarks>Distinct because it is the one failure the fetch loop retries by default.</remarks>
public sealed class PageTimeoutException : PageException
{
    public PageTimeoutException()
    {
    }

    public PageTimeoutException(string message)
        : base(message)
    {
    }

    public PageTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A navigation, or one of its redirect hops, was refused by the URL guard.</summary>
/// <remarks>
/// Never retried: the target is disallowed and trying again cannot change that.
/// Carries the offending hop rather than the originally requested URL, because
/// with a redirect chain those differ and only the hop is actionable.
/// </remarks>
public sealed class PageBlockedException : PageException
{
    public PageBlockedException(string url, RejectionReason reason, string detail = "")
        : base(detail.Length == 0
            ? $"blocked {url}: {reason.ToWireValue()}"
            : $"blocked {url}: {reason.ToWireValue()} ({detail})")
    {
        this.Url = url;
        this.Reason = reason;
        this.Detail = detail;
    }

    public PageBlockedException()
        : base()
    {
        this.Url = "";
        this.Detail = "";
    }

    public PageBlockedException(string message)
        : base(message)
    {
        this.Url = "";
        this.Detail = "";
    }

    public PageBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
        this.Url = "";
        this.Detail = "";
    }

    public string Url { get; }

    public RejectionReason Reason { get; }

    public string Detail { get; }
}

/// <summary>This page source cannot produce HTML dumps or screenshots.</summary>
public sealed class CaptureUnsupportedException : PageException
{
    public CaptureUnsupportedException()
    {
    }

    public CaptureUnsupportedException(string message)
        : base(message)
    {
    }

    public CaptureUnsupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A selector matched nothing on an otherwise healthy page.</summary>
/// <remarks>
/// Separate from a timeout so the fetch loop does not burn retries on a page
/// that loaded correctly and simply does not contain what was asked for.
/// </remarks>
public sealed class SelectorNotFoundException : PageException
{
    public SelectorNotFoundException()
    {
    }

    public SelectorNotFoundException(string message)
        : base(message)
    {
    }

    public SelectorNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

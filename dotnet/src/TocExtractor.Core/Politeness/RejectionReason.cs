using System.Text.Json;
using System.Text.Json.Serialization;

namespace TocExtractor.Core.Politeness;

/// <summary>Why a candidate URL was not fetched.</summary>
/// <remarks>
/// The wire strings are a cross-implementation contract rather than a display
/// detail: they are written into manifest.jsonl, and nothing in that file says
/// which implementation produced it. They are derived from the member names by
/// one naming policy so there is a single source of truth, and pinned
/// individually by tests against the Python <c>StrEnum</c> in politeness.py.
/// </remarks>
[JsonConverter(typeof(RejectionReasonConverter))]
public enum RejectionReason
{
    /// <summary>The DOM handed back something that was not a string.</summary>
    NotAString,

    /// <summary>The href was present but empty once trimmed.</summary>
    Empty,

    /// <summary>The value could not be parsed as a URL.</summary>
    Malformed,

    /// <summary>Parsed, but not http or https.</summary>
    DisallowedScheme,

    /// <summary>A URL with no host component.</summary>
    MissingHost,

    /// <summary>The host did not resolve.</summary>
    UnresolvableHost,

    /// <summary>The host resolves to loopback, link-local, or a private range.</summary>
    PrivateAddress,

    /// <summary>robots.txt disallows the path for this user agent.</summary>
    RobotsDisallowed,

    /// <summary>The same URL was already kept earlier in the collection.</summary>
    Duplicate,
}

/// <summary>Serialises <see cref="RejectionReason"/> as its manifest wire string.</summary>
/// <remarks>
/// Integer values are refused. A manifest carrying <c>6</c> where a consumer
/// expects <c>private_address</c> is a file that parses and means nothing.
/// </remarks>
internal sealed class RejectionReasonConverter()
    : JsonStringEnumConverter<RejectionReason>(RejectionReasonExtensions.WirePolicy, allowIntegerValues: false);

/// <summary>Wire-format access for callers that are not going through JSON.</summary>
public static class RejectionReasonExtensions
{
    internal static readonly JsonNamingPolicy WirePolicy = JsonNamingPolicy.SnakeCaseLower;

    /// <summary>The string this reason is recorded as, e.g. <c>private_address</c>.</summary>
    public static string ToWireValue(this RejectionReason reason) =>
        WirePolicy.ConvertName(reason.ToString());
}

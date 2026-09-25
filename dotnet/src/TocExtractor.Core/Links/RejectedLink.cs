using TocExtractor.Core.Politeness;

namespace TocExtractor.Core.Links;

/// <summary>One candidate that will not be fetched, and why.</summary>
/// <param name="Value">The candidate as it arrived, or a description of it when it was not a string.</param>
/// <param name="Reason">The counted reason.</param>
/// <param name="Detail">What names the specific cause: a scheme, a host, a robots rule.</param>
public readonly record struct RejectedLink(string Value, RejectionReason Reason, string Detail = "")
{
    public string Describe() =>
        this.Detail.Length == 0
            ? $"{this.Value}: {this.Reason.ToWireValue()}"
            : $"{this.Value}: {this.Reason.ToWireValue()} ({this.Detail})";
}

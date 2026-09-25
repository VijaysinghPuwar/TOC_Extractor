using TocExtractor.Core.Politeness;
using System.Text.Json;

namespace TocExtractor.Core.Tests.Politeness;

public sealed class RejectionReasonTests
{
    /// <summary>
    /// Every member and the exact string politeness.py writes for it. Spelled
    /// out rather than derived from the naming policy: a test that computes the
    /// expected value the way the code does would pass whatever the policy was
    /// changed to, which is the one failure this has to catch.
    /// </summary>
    private static readonly (RejectionReason Reason, string Wire)[] Contract =
    [
        (RejectionReason.NotAString, "not_a_string"),
        (RejectionReason.Empty, "empty"),
        (RejectionReason.Malformed, "malformed"),
        (RejectionReason.DisallowedScheme, "disallowed_scheme"),
        (RejectionReason.MissingHost, "missing_host"),
        (RejectionReason.UnresolvableHost, "unresolvable_host"),
        (RejectionReason.PrivateAddress, "private_address"),
        (RejectionReason.RobotsDisallowed, "robots_disallowed"),
        (RejectionReason.Duplicate, "duplicate"),
    ];

    public static TheoryData<RejectionReason, string> PythonContract()
    {
        var data = new TheoryData<RejectionReason, string>();
        foreach (var (reason, wire) in Contract)
        {
            data.Add(reason, wire);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PythonContract))]
    public void Wire_value_matches_the_python_enum(RejectionReason reason, string expected) =>
        Assert.Equal(expected, reason.ToWireValue());

    [Theory]
    [MemberData(nameof(PythonContract))]
    public void Serialises_as_the_wire_value(RejectionReason reason, string expected) =>
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(reason));

    [Theory]
    [MemberData(nameof(PythonContract))]
    public void Reads_back_a_manifest_written_by_the_python_implementation(
        RejectionReason expected,
        string wire) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<RejectionReason>($"\"{wire}\""));

    /// <summary>
    /// The table above is only a contract if it is exhaustive. A member added
    /// without a line in it fails here rather than reaching a manifest as an
    /// unreviewed string.
    /// </summary>
    [Fact]
    public void Contract_covers_every_member()
    {
        var declared = Enum.GetValues<RejectionReason>().Order();
        var pinned = Contract.Select(entry => entry.Reason).Order();

        Assert.Equal(declared, pinned);
    }

    [Fact]
    public void Wire_values_are_distinct()
    {
        var wire = Contract.Select(entry => entry.Wire).ToArray();

        Assert.Equal(wire.Length, wire.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// An integer in the reason position means a manifest that silently parses
    /// into a different reason the moment a member is reordered. It has to be
    /// refused, not coerced.
    /// </summary>
    [Fact]
    public void Refuses_an_integer_in_place_of_a_reason() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RejectionReason>("6"));

    [Fact]
    public void Refuses_a_string_that_is_not_a_known_reason() =>
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<RejectionReason>("\"banned\""));
}

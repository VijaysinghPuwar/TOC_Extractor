using System.Text;
using TocExtractor.Core.Text;

namespace TocExtractor.Core.Tests.Text;

public sealed class FilenameAllocatorTests
{
    [Fact]
    public void Deduplicates_a_case_only_collision()
    {
        var allocator = new FilenameAllocator();

        var first = allocator.Allocate(1, Golden.FileNameCase("case_collision_upper").Value);
        var second = allocator.Allocate(1, Golden.FileNameCase("case_collision_lower").Value);

        Assert.Equal(new AllocatedName("001 - Chapter One.txt"), first);
        Assert.True(second.Deduplicated);
        Assert.Equal("001 - Chapter One.txt", second.CollidedWith);
        Assert.Equal("001 - chapter one (2).txt", second.Name);
        Assert.NotEqual(
            first.Name, second.Name, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaves_distinct_titles_alone()
    {
        var allocator = new FilenameAllocator();

        Assert.False(allocator.Allocate(1, "Alpha").Deduplicated);
        Assert.False(allocator.Allocate(2, "Beta").Deduplicated);
    }

    /// <summary>The NNN prefix means the common case never reaches the dedup path.</summary>
    [Fact]
    public void Index_alone_keeps_identical_titles_distinct()
    {
        var allocator = new FilenameAllocator();

        Assert.False(allocator.Allocate(1, "Chapter").Deduplicated);
        Assert.False(allocator.Allocate(2, "Chapter").Deduplicated);
    }

    private static readonly string[] CaseVariants = ["Ch", "CH", "ch"];

    [Fact]
    public void Third_collision_gets_its_own_suffix()
    {
        var allocator = new FilenameAllocator();

        string[] names = [.. CaseVariants.Select(title => allocator.Allocate(1, title).Name)];

        Assert.Equal(["001 - Ch.txt", "001 - CH (2).txt", "001 - ch (3).txt"], names);
        Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Why collapsing runs of forbidden characters is safe: "A//B" and "A/B"
    /// both sanitise to "A_B", and without the allocator the second would
    /// overwrite the first.
    /// </summary>
    [Fact]
    public void Catches_a_collision_the_run_collapse_created()
    {
        Assert.Equal("A_B", FileName.Sanitise("A//B"));
        Assert.Equal("A_B", FileName.Sanitise("A/B"));

        var allocator = new FilenameAllocator();
        var first = allocator.Allocate(7, "A//B");
        var second = allocator.Allocate(7, "A/B");

        Assert.Equal("007 - A_B.txt", first.Name);
        Assert.True(second.Deduplicated);
        Assert.Equal("007 - A_B (2).txt", second.Name);
    }

    [Fact]
    public void Allocated_component_respects_the_byte_limit()
    {
        var result = new FilenameAllocator().Allocate(1, new string('章', 300));

        Assert.True(Encoding.UTF8.GetByteCount(result.Name) <= FileName.MaxBytes);
        Assert.StartsWith("001 - ", result.Name, StringComparison.Ordinal);
        Assert.EndsWith(".txt", result.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Dedup_marker_does_not_push_the_component_over_the_byte_limit()
    {
        var allocator = new FilenameAllocator();
        var title = new string('章', 300);

        var first = allocator.Allocate(1, title);
        var second = allocator.Allocate(1, title);

        Assert.True(second.Deduplicated);
        Assert.True(Encoding.UTF8.GetByteCount(first.Name) <= FileName.MaxBytes);
        Assert.True(Encoding.UTF8.GetByteCount(second.Name) <= FileName.MaxBytes);
    }

    [Fact]
    public void Honours_a_non_default_suffix()
    {
        var result = new FilenameAllocator(".md").Allocate(4, "Chapter Four");

        Assert.Equal("004 - Chapter Four.md", result.Name);
    }
}

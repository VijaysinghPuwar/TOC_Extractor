using TocExtractor.App.Scanning;

namespace TocExtractor.App.Tests;

public sealed class ScannerFirstChapterTests
{
    private static NovelScanner.ChapterLink Link(long id, int number, string title) =>
        new($"https://novel.example/book-1/{id}.html", number, title);

    [Fact]
    public void A_second_parts_chapter_1_is_not_taken_for_the_books_first_chapter()
    {
        // A newest-first list of a book in two parts: Book 1 ends, Book 2 begins.
        NovelScanner.ChapterLink[] listed =
        [
            Link(3256180, 46, "Book 1: Chapter 46"),
            Link(3256185, 51, "Book 1: Chapter 51"),
            Link(3256189, 1, "Book 2: Chapter 1"),
            Link(3256190, 2, "Book 2: Chapter 2"),
        ];

        Assert.False(NovelScanner.HasTrueChapterOne(listed));
    }

    [Fact]
    public void A_listed_chapter_1_that_comes_first_is_trusted()
    {
        NovelScanner.ChapterLink[] listed = [Link(100, 1, "Chapter 1"), Link(101, 2, "Chapter 2")];

        Assert.True(NovelScanner.HasTrueChapterOne(listed));
    }

    [Fact]
    public void Without_ids_a_chapter_labelled_1_is_trusted_as_before()
    {
        NovelScanner.ChapterLink[] listed =
        [
            new("https://novel.example/book/chapter-one", 1, "Chapter 1"),
            new("https://novel.example/book/chapter-two", 2, "Chapter 2"),
        ];

        Assert.True(NovelScanner.HasTrueChapterOne(listed));
    }

    [Fact]
    public void No_chapter_1_listed_means_look_for_it()
    {
        Assert.False(NovelScanner.HasTrueChapterOne([Link(5, 46, "Chapter 46")]));
    }
}

public sealed class PartNumberingTests
{
    private static NovelScanner.ChapterLink Link(long id, int number, string title) =>
        new($"https://novel.example/book-1/{id}.html", number, title);

    [Fact]
    public void Arcs_that_restart_their_numbers_are_numbered_by_place_when_the_site_gives_a_total()
    {
        NovelScanner.ChapterLink[] listed =
        [
            Link(3211814, 14, "Arc 9: Chapter 14: Pernicious"),
            Link(3213431, 15, "Arc 9: Chapter 15: Stain"),
            Link(3277675, 38, "Arc 9: Chapter 38: Heart-Thief"),
        ];

        Assert.True(NovelScanner.RestartsNumbering(listed, total: 334));
    }

    [Fact]
    public void Volume_labels_on_a_book_numbered_straight_through_change_nothing()
    {
        NovelScanner.ChapterLink[] listed =
        [
            Link(1, 48, "Volume 1 Chapter 48: Clown"),
            Link(2, 49, "Volume 1 Chapter 49: Tarot"),
            Link(3, 50, "Volume 1 Chapter 50: Sequence"),
        ];

        Assert.False(NovelScanner.RestartsNumbering(listed, total: 50));
    }

    [Fact]
    public void Without_the_sites_total_the_titles_are_kept()
    {
        NovelScanner.ChapterLink[] listed =
        [
            Link(1, 1, "Book 2: Chapter 1"),
            Link(2, 2, "Book 2: Chapter 2"),
            Link(3, 3, "Book 2: Chapter 3"),
        ];

        Assert.False(NovelScanner.RestartsNumbering(listed, total: null));
    }

    [Fact]
    public void Plain_titles_are_never_part_scoped()
    {
        NovelScanner.ChapterLink[] listed =
        [
            Link(1, 10, "Chapter 10: The Storm"),
            Link(2, 11, "Chapter 11: Calm"),
            Link(3, 12, "Chapter 12: After"),
        ];

        Assert.False(NovelScanner.RestartsNumbering(listed, total: 400));
    }
}

public sealed class PlaceNumberedPlanTests
{
    private static Scanning.ScanResult Scan(params (int Number, long Id)[] chapters) => new()
    {
        NovelUrl = "https://novel.example/book",
        BookTitle = "Book",
        Chapters = [.. chapters.Select(c => new Scanning.ScannedChapter(c.Number, $"Part {c.Number}", $"https://novel.example/book/{c.Id}.html"))],
        Layout = new Scanning.ChapterLayout("h1", "article", "a.next", "a.prev"),
        PositionalNumbers = true,
    };

    [Fact]
    public void A_range_before_the_listed_chapters_is_counted_from_chapter_1_alone()
    {
        var scan = Scan((1, 100), (48, 146), (49, 147), (50, 148), (71, 170));

        var plan = Scanning.RangePlanner.Plan(scan, 1, 50);

        Assert.Empty(plan.Direct);
        var walk = Assert.Single(plan.Walks);
        Assert.Equal("https://novel.example/book/100.html", walk.StartUrl);
        Assert.Equal((1, 50), (walk.SaveFrom, walk.SaveTo));
    }

    [Fact]
    public void A_range_inside_the_listed_chapters_opens_them_directly()
    {
        var scan = Scan((1, 100), (48, 146), (49, 147), (50, 148), (71, 170));

        var plan = Scanning.RangePlanner.Plan(scan, 48, 50);

        Assert.Equal([48, 49, 50], plan.Direct.Select(d => d.Number));
        Assert.Empty(plan.Walks);
    }
}

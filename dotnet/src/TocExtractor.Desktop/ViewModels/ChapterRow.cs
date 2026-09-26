using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TocExtractor.Desktop.ViewModels;

public enum ChapterState
{
    Waiting,
    Saved,
    Failed,

    /// <summary>Written by an earlier run and not fetched again.</summary>
    AlreadySaved,
}

/// <summary>One line in the chapter list.</summary>
public sealed partial class ChapterRow(int number, string url) : ObservableObject
{
    public int Number { get; } = number;

    public string NumberText => this.Number.ToString("D3", CultureInfo.InvariantCulture);

    public string Url { get; } = url;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial string? Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReadable), nameof(IsFailed), nameof(Symbol))]
    public partial ChapterState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial int Words { get; set; }

    /// <summary>Lines long enough to be story that were on the page but are not in the saved text.</summary>
    public IReadOnlyList<string> LeftOutLines { get; set; } = [];

    /// <summary>Words on the page that were not saved.</summary>
    public int LeftOutWords { get; set; }

    /// <summary>Saved, but a line long enough to be story was on the page and is not in the text: worth a look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(NeedsLook), nameof(Symbol))]
    public partial bool LeftOut { get; set; }

    /// <summary>Saved, but some paragraph appears more often than on the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(NeedsLook), nameof(Symbol))]
    public partial bool Doubled { get; set; }

    /// <summary>Saved with the same text as another chapter: likely one page shown for both.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(NeedsLook), nameof(Symbol))]
    public partial int SameTextAs { get; set; }

    /// <summary>Saved, but something about it should be checked.</summary>
    public bool NeedsLook => this.LeftOut || this.Doubled || this.SameTextAs > 0 || this.Short;

    /// <summary>Saved, but far shorter than the book's other chapters: worth a look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(NeedsLook), nameof(Symbol))]
    public partial bool Short { get; set; }

    /// <summary>The cleaned chapter text, held only when it was not saved to a file the reader can open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadable))]
    public partial string? Text { get; set; }

    /// <summary>
    /// The chapter's own file, which the reader opens when the chapter is
    /// shown. Keeping every chapter's text in memory instead cost about
    /// 60 MB per 1,000 chapters, for text a person reads a few of.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadable))]
    public partial string? File { get; set; }

    /// <summary>How long the text is, in characters: the end of the file, less its closing newline.</summary>
    public int TextLength { get; set; }

    /// <summary>The chapter's text, from memory or from its file; null if neither has it.</summary>
    /// <remarks>
    /// A chapter's file is its heading, a blank line, and then the text and
    /// one newline, so the text is the file's last <see cref="TextLength"/>
    /// characters before that newline. A file that no longer fits (edited by
    /// hand) is shown whole rather than cut in the wrong place.
    /// </remarks>
    public string? ReadText()
    {
        if (this.Text is not null || this.File is not { } file)
        {
            return this.Text;
        }

        string raw;
        try
        {
            raw = System.IO.File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return raw.EndsWith('\n') && raw.Length > this.TextLength
            ? raw.Substring(raw.Length - 1 - this.TextLength, this.TextLength)
            : raw;
    }

    public string Heading => string.IsNullOrWhiteSpace(this.Title)
        ? (this.Url.Length > 0 ? this.Url : string.Create(CultureInfo.InvariantCulture, $"Chapter {this.Number}"))
        : this.Title;

    public bool IsReadable => this.State == ChapterState.Saved && (this.Text is not null || this.File is not null);

    public bool IsFailed => this.State == ChapterState.Failed;

    /// <summary>A plain glyph per state, so the list reads without relying on colour.</summary>
    public string Symbol => this.State switch
    {
        ChapterState.Saved when this.NeedsLook => "!",
        ChapterState.Saved => "●",
        ChapterState.AlreadySaved => "◐",
        ChapterState.Failed => "×",
        _ => "○",
    };

    public string StatusText => this.State switch
    {
        ChapterState.Saved when this.LeftOut => string.Create(CultureInfo.InvariantCulture, $"Saved, but {this.LeftOutWords:N0} words on the page were left out: check it"),
        ChapterState.Saved when this.Doubled => "Saved, but some text appears twice: check it",
        ChapterState.Saved when this.SameTextAs > 0 => string.Create(CultureInfo.InvariantCulture, $"Saved, but the same text as chapter {this.SameTextAs}: check it"),
        ChapterState.Saved when this.Short => string.Create(CultureInfo.InvariantCulture, $"Saved, only {this.Words:N0} words: check it"),
        ChapterState.Saved => string.Create(CultureInfo.InvariantCulture, $"Saved, {this.Words:N0} words"),
        ChapterState.AlreadySaved => "Saved in an earlier run",
        ChapterState.Failed => "Failed: " + (this.Detail ?? "unknown error"),
        _ => "Waiting",
    };
}

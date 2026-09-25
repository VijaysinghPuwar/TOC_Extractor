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

    /// <summary>The cleaned chapter text, held for the reader once it is saved.</summary>
    [ObservableProperty]
    public partial string? Text { get; set; }

    public string Heading => string.IsNullOrWhiteSpace(this.Title) ? this.Url : this.Title;

    public bool IsReadable => this.State == ChapterState.Saved && this.Text is not null;

    public bool IsFailed => this.State == ChapterState.Failed;

    /// <summary>A plain glyph per state, so the list reads without relying on colour.</summary>
    public string Symbol => this.State switch
    {
        ChapterState.Saved => "●",
        ChapterState.AlreadySaved => "◐",
        ChapterState.Failed => "×",
        _ => "○",
    };

    public string StatusText => this.State switch
    {
        ChapterState.Saved => string.Create(CultureInfo.InvariantCulture, $"Saved, {this.Words:N0} words"),
        ChapterState.AlreadySaved => "Saved in an earlier run",
        ChapterState.Failed => "Failed: " + (this.Detail ?? "unknown error"),
        _ => "Waiting",
    };
}

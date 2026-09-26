using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TocExtractor.App.Session;

namespace TocExtractor.Desktop.ViewModels;

/// <summary>One site in Settings that has asked to check the person is human, and how it is read.</summary>
public sealed partial class SitePaceRow(SitePace pace, SitePaces store) : ObservableObject
{
    public string Site { get; } = pace.Site;

    public string Host => Uri.TryCreate(this.Site, UriKind.Absolute, out var uri) ? uri.Host : this.Site;

    public string Detail =>
        string.Create(CultureInfo.InvariantCulture, $"Asked {pace.Checks} time(s)")
        + (pace.LastCheck is { } last ? ", last on " + last.LocalDateTime.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture) : "")
        + ".";

    /// <summary>Careful: a burst, then a page every 12 seconds, which keeps checks away. Off: full speed, with a check to click now and then.</summary>
    [ObservableProperty]
    public partial bool Careful { get; set; } = pace.Careful;

    partial void OnCarefulChanged(bool value) => store.SetCareful(this.Site, value);

    [RelayCommand]
    private void Forget() => store.Forget(this.Site);
}

using System.Text.Json;
using TocExtractor.App;

namespace TocExtractor.Desktop.Services;

/// <summary>What the window remembers between launches.</summary>
public sealed record DesktopSettings
{
    public string NovelUrl { get; init; } = "";

    public string OutputDirectory { get; init; } = AppPaths.DefaultOutputDirectory;

    public bool Text { get; init; } = true;

    public bool Pdf { get; init; }

    /// <summary>Write a CSV log of every scan and download next to the book.</summary>
    public bool KeepLog { get; init; } = true;

    /// <summary>Chapters fetched at once. One is kindest to a site and least likely to trip its checks.</summary>
    public int AtOnce { get; init; } = 1;

    public double MinDelay { get; init; } = 2;

    public double MaxDelay { get; init; } = 4;

    public bool IncludeLinks { get; init; }

    public bool StripAds { get; init; } = true;

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; init; } = "System";
}

/// <summary>Remembers the form between launches, so a person picks up where they left off.</summary>
/// <remarks>
/// Best effort by design: a settings file that cannot be read or written must
/// never stop the app from opening, so every failure here is swallowed and
/// the defaults are used.
/// </remarks>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public DesktopSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), Options) ?? new DesktopSettings()
                : new DesktopSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DesktopSettings();
        }
    }

    public void Save(DesktopSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting anyone over.
        }
    }
}

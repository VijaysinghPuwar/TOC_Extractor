using System.Text.Json;
using TocExtractor.App.Session;

namespace TocExtractor.Desktop.Services;

/// <summary>Remembers the form between launches, so a person picks up where they left off.</summary>
/// <remarks>
/// Best effort by design: a settings file that cannot be read or written must
/// never stop the app from opening, so every failure here is swallowed and
/// the defaults are used.
/// </remarks>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public SessionSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(path), Options) ?? new SessionSettings()
                : new SessionSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SessionSettings();
        }
    }

    public void Save(SessionSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings with { Force = false }, Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting anyone over.
        }
    }
}

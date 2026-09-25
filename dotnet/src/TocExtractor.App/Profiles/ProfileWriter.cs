using System.Globalization;
using System.Text;

namespace TocExtractor.App.Profiles;

/// <summary>Writes a profile in the TOML that <see cref="ProfileLoader"/> and the Python tool read.</summary>
/// <remarks>
/// Written by hand rather than serialised, so the file keeps the layout of
/// profiles/example.toml: people open these in a text editor, and a key order
/// that changes on every save makes a diff of two profiles unreadable.
/// </remarks>
public static class ProfileWriter
{
    public static string Render(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var toml = new StringBuilder();
        toml.AppendLine("# A TOC Extractor profile. Works in the desktop app and on the command line:");
        toml.AppendLine("#   toc-extractor --profile this-file.toml --toc https://...");
        toml.AppendLine();
        toml.AppendLine("[selectors]");
        Line(toml, "link", Quote(profile.Link));
        Line(toml, "title", Quote(profile.Title));
        Line(toml, "content", Quote(profile.Content));
        toml.AppendLine();
        toml.AppendLine("[options]");
        Line(toml, "max", Number(profile.Max));
        Line(toml, "out", Quote(profile.Out));
        Line(toml, "concurrency", Number(profile.Concurrency));
        Line(toml, "retries", Number(profile.Retries));
        Line(toml, "min_delay", Number(profile.MinDelay));
        Line(toml, "max_delay", Number(profile.MaxDelay));
        Line(toml, "wait_after_load", Number(profile.WaitAfterLoad));
        Line(toml, "timeout", Number(profile.Timeout));
        Line(toml, "include_links", profile.IncludeLinks is { } links ? (links ? "true" : "false") : null);
        Line(
            toml,
            "formats",
            profile.Formats is { } formats ? "[" + string.Join(", ", formats.Select(Quote)) + "]" : null);
        Line(toml, "ua", Quote(profile.UserAgent));
        return toml.ToString();
    }

    public static void Save(Profile profile, string path)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside and moved into place, so a crash mid-write cannot
        // leave a half profile that then refuses to load.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Render(profile), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static void Line(StringBuilder toml, string key, string? value)
    {
        if (value is not null)
        {
            toml.Append(key).Append(" = ").AppendLine(value);
        }
    }

    private static string? Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    private static string? Number(double? value) =>
        value is { } number
            ? number.ToString(number % 1 == 0 ? "0.0" : "R", CultureInfo.InvariantCulture)
            : null;

    /// <summary>A TOML basic string, escaped per the spec.</summary>
    private static string? Quote(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var quoted = new StringBuilder("\"");
        foreach (var character in value)
        {
            quoted.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => string.Create(
                    CultureInfo.InvariantCulture, $"\\u{(int)character:X4}"),
                _ => character.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }
}

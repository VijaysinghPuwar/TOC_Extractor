using System.Globalization;
using Tomlyn;
using Tomlyn.Model;

namespace TocExtractor.Cli;

/// <summary>A profile that cannot be used, with a message naming the problem.</summary>
public sealed class ProfileException(string message) : Exception(message);

/// <summary>A parsed selector profile. Absent values stay null so they cannot mask a flag.</summary>
public sealed record Profile
{
    public required string Path { get; init; }

    public string? Link { get; init; }

    public string? Title { get; init; }

    public string? Content { get; init; }

    public int? Max { get; init; }

    public string? Out { get; init; }

    public int? Concurrency { get; init; }

    public int? Retries { get; init; }

    public double? MinDelay { get; init; }

    public double? MaxDelay { get; init; }

    public int? WaitAfterLoad { get; init; }

    public int? Timeout { get; init; }

    public bool? IncludeLinks { get; init; }

    public IReadOnlyList<string>? Formats { get; init; }

    public string? UserAgent { get; init; }
}

/// <summary>
/// Reads a profile, refusing anything malformed rather than half-applying it.
/// </summary>
/// <remarks>
/// Every value is type-checked here. Python validates only the format list, so
/// <c>max = "twenty"</c> loads without complaint and dies later with a
/// TypeError from deep inside the option translation — a traceback where a
/// sentence naming the file, the key and the expected type belongs.
/// </remarks>
public static class ProfileLoader
{
    private static readonly string[] SelectorKeys = ["link", "title", "content"];

    /// <summary>Profile key to the flag that overrides it.</summary>
    /// <remarks>
    /// Explicit flags always win, so a profile is a starting point rather than
    /// something to edit in order to run one different command.
    /// </remarks>
    internal static readonly Dictionary<string, string> OptionFlags = new(StringComparer.Ordinal)
    {
        ["max"] = "--max",
        ["out"] = "--out",
        ["concurrency"] = "--concurrency",
        ["retries"] = "--retries",
        ["min_delay"] = "--min-delay",
        ["max_delay"] = "--max-delay",
        ["wait_after_load"] = "--wait-after-load",
        ["timeout"] = "--timeout",
        ["include_links"] = "--include-links",
        ["formats"] = "--format",
        ["ua"] = "--ua",
    };

    public static Profile Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            throw new ProfileException($"no profile at {path}");
        }
        catch (DirectoryNotFoundException)
        {
            throw new ProfileException($"no profile at {path}");
        }
        catch (IOException exception)
        {
            throw new ProfileException($"could not read {path}: {exception.Message}");
        }

        return Parse(text, path);
    }

    internal static Profile Parse(string text, string path)
    {
        Dictionary<string, object?>? document;
        try
        {
            document = TomlSerializer.Deserialize<Dictionary<string, object?>>(
                text, new TomlSerializerOptions());
        }
        catch (TomlException exception)
        {
            throw new ProfileException(
                $"{path} is not valid TOML: {exception.Message.Split('\n')[0]}");
        }

        document ??= [];

        var selectors = Table(document, "selectors", path);
        Reject(selectors, SelectorKeys, path, "[selectors]");

        var options = Table(document, "options", path);
        Reject(options, [.. OptionFlags.Keys], path, "[options]");

        return new Profile
        {
            Path = path,
            Link = String(selectors, "link", path),
            Title = String(selectors, "title", path),
            Content = String(selectors, "content", path),
            Max = Integer(options, "max", path),
            Out = String(options, "out", path),
            Concurrency = Integer(options, "concurrency", path),
            Retries = Integer(options, "retries", path),
            MinDelay = Number(options, "min_delay", path),
            MaxDelay = Number(options, "max_delay", path),
            WaitAfterLoad = Integer(options, "wait_after_load", path),
            Timeout = Integer(options, "timeout", path),
            IncludeLinks = Boolean(options, "include_links", path),
            Formats = Strings(options, "formats", path),
            UserAgent = String(options, "ua", path),
        };
    }

    private static IDictionary<string, object?> Table(
        Dictionary<string, object?> document, string name, string path)
    {
        if (!document.TryGetValue(name, out var value))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return value as IDictionary<string, object?>
            ?? throw new ProfileException($"{path}: [{name}] must be a table");
    }

    private static void Reject(IDictionary<string, object?> table, string[] known, string path, string section)
    {
        string[] unknown = [.. table.Keys.Where(key => !known.Contains(key, StringComparer.Ordinal)).Order(StringComparer.Ordinal)];
        if (unknown.Length > 0)
        {
            // A typo that is silently ignored is a profile that does not do
            // what it says, which is worse than one that refuses to load.
            throw new ProfileException(
                $"{path}: unknown key(s) in {section}: {string.Join(", ", unknown)}. "
                + $"Valid keys: {string.Join(", ", known.Order(StringComparer.Ordinal))}");
        }
    }

    private static string? String(IDictionary<string, object?> table, string key, string path) =>
        !table.TryGetValue(key, out var value)
            ? null
            : value as string ?? throw Wrong(path, key, "a string", value);

    private static int? Integer(IDictionary<string, object?> table, string key, string path)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
            long number => throw new ProfileException(
                $"{path}: [options].{key} is out of range ({number})"),
            _ => throw Wrong(path, key, "a whole number", value),
        };
    }

    private static double? Number(IDictionary<string, object?> table, string key, string path)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            long number => number,
            double number => number,
            _ => throw Wrong(path, key, "a number", value),
        };
    }

    private static bool? Boolean(IDictionary<string, object?> table, string key, string path) =>
        !table.TryGetValue(key, out var value)
            ? null
            : value as bool? ?? throw Wrong(path, key, "true or false", value);

    private static IReadOnlyList<string>? Strings(IDictionary<string, object?> table, string key, string path)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is not TomlArray array || array.Any(item => item is not string))
        {
            throw Wrong(path, key, "a list of strings", value);
        }

        return [.. array.Select(item => (string)item!)];
    }

    private static ProfileException Wrong(string path, string key, string expected, object? actual) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"{path}: [options].{key} must be {expected}, but is {Describe(actual)}"));

    private static string Describe(object? value) => value switch
    {
        null => "empty",
        string text => $"the string \"{text}\"",
        bool flag => flag ? "true" : "false",
        TomlArray => "a list",
        IDictionary<string, object?> => "a table",
        _ => string.Create(CultureInfo.InvariantCulture, $"{value}"),
    };
}

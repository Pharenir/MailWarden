using System.Collections.Frozen;
using System.Text.Json;
using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Loads <see cref="ClassifierSettings"/> from the JSON settings file below the data root. Strict on purpose (fail
/// fast): a typo in a key or a wrong value type is an error, never silently ignored.
/// </summary>
public static class ClassifierSettingsFile
{
    /// <summary>File name below the data root (the composition root combines it with DataPaths.Root in AP-04).</summary>
    public const string FileName = "classifier-settings.json";

    private const string LikelySpamThresholdKey = "likelySpamThreshold";
    private const string CertainSpamThresholdKey = "certainSpamThreshold";
    private const string SuspiciousTldsKey = "suspiciousTlds";
    private const string FreemailDomainsKey = "freemailDomains";
    private const int MaximumShownKeyLength = 100;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the settings. A missing file gives <see cref="ClassifierSettings.Default"/>. The file is a JSON object
    /// with optional keys (camelCase, case-sensitive): "likelySpamThreshold": int, "certainSpamThreshold": int,
    /// "suspiciousTlds": [string], "freemailDomains": [string]. A missing key keeps the default value; a given list
    /// replaces the default list completely (no merge). Comments and trailing commas are allowed. The result is
    /// validated with <see cref="ClassifierSettings.Validate"/>.
    /// </summary>
    /// <param name="path">Full path of the settings file.</param>
    /// <returns>The loaded and validated settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidDataException">
    /// Invalid JSON, root not an object, unknown or duplicate key, wrong value type, null list entry, or failed
    /// validation (the <see cref="ArgumentException"/> of <see cref="ClassifierSettings.Validate"/> is the inner
    /// exception). The message names the file name (never the full path) and, where known, the key.
    /// </exception>
    public static ClassifierSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return ClassifierSettings.Default;
        }

        // Only the file name goes into messages: the full path contains the user profile.
        var fileName = Path.GetFileName(path);
        var settings = Parse(File.ReadAllText(path), fileName);
        try
        {
            settings.Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Classifier settings file '{fileName}' is invalid: {exception.Message}", exception);
        }

        return settings;
    }

    // Reads the JSON text; every key that is absent keeps its value from ClassifierSettings.Default.
    private static ClassifierSettings Parse(string json, string fileName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Classifier settings file '{fileName}' is not valid JSON (line {exception.LineNumber + 1}, "
                + $"byte {exception.BytePositionInLine + 1}).",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Classifier settings file '{fileName}' must contain a JSON object at the top level.");
            }

            var settings = ClassifierSettings.Default;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seenKeys.Add(property.Name))
                {
                    throw InvalidKey(fileName, property.Name, "is given more than once");
                }

                settings = property.Name switch
                {
                    LikelySpamThresholdKey => settings with { LikelySpamThreshold = ReadInt32(property, fileName) },
                    CertainSpamThresholdKey => settings with { CertainSpamThreshold = ReadInt32(property, fileName) },
                    SuspiciousTldsKey => settings with { SuspiciousTlds = ReadStringSet(property, fileName) },
                    FreemailDomainsKey => settings with { FreemailDomains = ReadStringSet(property, fileName) },
                    _ => throw InvalidKey(fileName, property.Name, "is not a known setting"),
                };
            }

            return settings;
        }
    }

    // A whole JSON number in the Int32 range; strings, decimals and null are wrong types.
    private static int ReadInt32(JsonProperty property, string fileName)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
        {
            throw InvalidKey(fileName, property.Name, "must be a whole number");
        }

        return number;
    }

    // A JSON array of strings; replaces the default list completely. Empty strings are left to Validate().
    private static FrozenSet<string> ReadStringSet(JsonProperty property, string fileName)
    {
        if (property.Value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidKey(fileName, property.Name, "must be an array of strings");
        }

        var entries = new List<string>();
        foreach (var element in property.Value.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    entries.Add(element.GetString()!);
                    break;
                case JsonValueKind.Null:
                    throw InvalidKey(fileName, property.Name, "must not contain null entries");
                default:
                    throw InvalidKey(fileName, property.Name, "must contain only strings");
            }
        }

        return entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    // Key names are echoed truncated: an unknown key is file content of arbitrary length. A surrogate pair is never
    // split.
    private static InvalidDataException InvalidKey(string fileName, string key, string problem)
    {
        var shownKey = key;
        if (key.Length > MaximumShownKeyLength)
        {
            var cutLength = char.IsHighSurrogate(key[MaximumShownKeyLength - 1])
                ? MaximumShownKeyLength - 1
                : MaximumShownKeyLength;
            shownKey = string.Concat(key.AsSpan(0, cutLength), "…");
        }

        return new InvalidDataException($"Classifier settings file '{fileName}': key '{shownKey}' {problem}.");
    }
}

using System.Text.Json;
using MailWarden.Core.Classification;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Loads <see cref="ClassificationPolicy"/> from the JSON policy file below the data root. As strict as
/// <see cref="ClassifierSettingsFile"/>: this file decides whether mail moves and is deleted, so a typo must never
/// silently fall back to another behaviour.
/// </summary>
public static class ClassificationPolicyFile
{
    /// <summary>File name below the data root.</summary>
    public const string FileName = "classification-policy.json";

    private const string ModeKey = "mode";
    private const string QuarantineRetentionDaysKey = "quarantineRetentionDays";
    private const string RecordOnlyValue = "recordOnly";
    private const string ActValue = "act";

    // Comments are rejected (the parser's default), unlike in the classifier settings: a commented-out line must not
    // change whether mail moves or is deleted without anyone noticing.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the policy. A missing file gives <see cref="ClassificationPolicy.Default"/>. The file is a JSON object with
    /// optional keys (camelCase, case-sensitive): "mode": "recordOnly" or "act", "quarantineRetentionDays": int. A
    /// missing key keeps its default. Comments are an error, trailing commas are allowed. The result is validated with
    /// <see cref="ClassificationPolicy.Validate"/>.
    /// </summary>
    /// <param name="path">Full path of the policy file.</param>
    /// <returns>The loaded and validated policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidDataException">
    /// Invalid JSON (including comments), root not an object, unknown or duplicate key, wrong value type or invalid value
    /// (mode values are exact and case-sensitive). The message names
    /// the file name (never the full path) and, where known, the key; never the value.
    /// </exception>
    public static ClassificationPolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return ClassificationPolicy.Default;
        }

        // Only the file name goes into messages: the full path contains the user profile.
        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    // Reads the JSON text; every key that is absent keeps its value from ClassificationPolicy.Default.
    private static ClassificationPolicy Parse(string json, string fileName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Classification policy file '{fileName}' is not valid JSON (line {exception.LineNumber + 1}, "
                + $"byte {exception.BytePositionInLine + 1}).",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Classification policy file '{fileName}' must contain a JSON object at the top level.");
            }

            var policy = ClassificationPolicy.Default;
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seenKeys.Add(property.Name))
                {
                    throw InvalidKey(fileName, property.Name, "is given more than once");
                }

                policy = property.Name switch
                {
                    ModeKey => policy with { Mode = ReadMode(property, fileName) },
                    QuarantineRetentionDaysKey => policy with { QuarantineRetentionDays = ReadRetentionDays(property, fileName) },
                    _ => throw InvalidKey(fileName, property.Name, "is not a known setting"),
                };
            }

            return policy;
        }
    }

    private static ClassificationMode ReadMode(JsonProperty property, string fileName)
    {
        string? value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        return value switch
        {
            RecordOnlyValue => ClassificationMode.RecordOnly,
            ActValue => ClassificationMode.Act,
            _ => throw InvalidKey(fileName, property.Name, $"must be \"{RecordOnlyValue}\" or \"{ActValue}\""),
        };
    }

    // A whole JSON number in the allowed range; strings, decimals and null are wrong types. The range is checked with
    // Validate so that the rule lives in one place.
    private static int ReadRetentionDays(JsonProperty property, string fileName)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var days))
        {
            throw InvalidKey(fileName, property.Name, "must be a whole number");
        }

        try
        {
            (ClassificationPolicy.Default with { QuarantineRetentionDays = days }).Validate();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Classification policy file '{fileName}': key '{property.Name}' is invalid: {exception.Message}",
                exception);
        }

        return days;
    }

    // Key names are echoed truncated: an unknown key is file content of arbitrary length.
    private static InvalidDataException InvalidKey(string fileName, string key, string problem)
    {
        return new InvalidDataException(
            $"Classification policy file '{fileName}': key '{DisplayText.Truncate(key)}' {problem}.");
    }
}

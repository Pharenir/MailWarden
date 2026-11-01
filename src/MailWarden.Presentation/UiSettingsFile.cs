using System.Text.Json;
using MailWarden.Core.Classification;
using MailWarden.Core.Text;

namespace MailWarden.Presentation;

/// <summary>
/// <see cref="IUiSettingsStore"/> on a JSON file. Strict on purpose: a typo in a key or a wrong type is reported, never
/// silently ignored. Keys: <c>layout</c> ("sideBySide", "listAbove"), <c>theme</c> ("system", "light", "dark"),
/// <c>navigationWidth</c>, <c>listSize</c> (numbers within the limits of <see cref="UiSettings"/>).
/// </summary>
public sealed class UiSettingsFile : IUiSettingsStore
{
    /// <summary>File name below the data root.</summary>
    public const string FileName = "ui-settings.json";

    private const string LayoutKey = "layout";
    private const string ThemeKey = "theme";
    private const string NavigationWidthKey = "navigationWidth";
    private const string ListSizeKey = "listSize";

    private const string SideBySideValue = "sideBySide";
    private const string ListAboveValue = "listAbove";
    private const string SystemValue = "system";
    private const string LightValue = "light";
    private const string DarkValue = "dark";

    private readonly string path;

    /// <summary>Creates the store. Does not touch the file system.</summary>
    /// <param name="path">Path of the settings file itself, not of its folder.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    public UiSettingsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    /// <summary>Only the file name goes into messages: the full path contains the user profile.</summary>
    private string DisplayFileName => Path.GetFileName(path);

    /// <summary>Loads the settings. A missing file gives defaults, and missing keys their defaults.</summary>
    /// <returns>The settings.</returns>
    /// <exception cref="UiSettingsException">
    /// Unreadable file, invalid JSON, an unknown or repeated key, a wrong type or a value out of range. The message names
    /// the file name and the key, without values.
    /// </exception>
    public UiSettings Load()
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return UiSettings.Default;
            }

            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UiSettingsException($"Settings file '{DisplayFileName}' cannot be read ({exception.GetType().Name}).");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new UiSettingsException($"Settings file '{DisplayFileName}' is not valid JSON.");
        }

        using (document)
        {
            return Parse(document.RootElement);
        }
    }

    /// <summary>Stores the settings atomically (temporary file + move) as UTF-8 without BOM.</summary>
    /// <param name="settings">Settings to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
    /// <exception cref="UiSettingsException">A value is out of range; the file would not load again.</exception>
    /// <exception cref="IOException">Writing failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Writing is not allowed.</exception>
    public void Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureSizeInRange(settings.NavigationWidth, NavigationWidthKey);
        EnsureSizeInRange(settings.ListSize, ListSizeKey);

        using var content = new MemoryStream();
        using (var writer = new Utf8JsonWriter(content, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(LayoutKey, ToValue(settings.Layout));
            writer.WriteString(ThemeKey, ToValue(settings.Theme));
            writer.WriteNumber(NavigationWidthKey, settings.NavigationWidth);
            writer.WriteNumber(ListSizeKey, settings.ListSize);
            writer.WriteEndObject();
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content.ToArray());
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            // After a successful move the temporary file no longer exists.
            File.Delete(temporaryPath);
        }
    }

    private static string ToValue(ReadingPaneLayout layout) => layout switch
    {
        ReadingPaneLayout.SideBySide => SideBySideValue,
        ReadingPaneLayout.ListAbove => ListAboveValue,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown layout."),
    };

    private static string ToValue(AppTheme theme) => theme switch
    {
        AppTheme.System => SystemValue,
        AppTheme.Light => LightValue,
        AppTheme.Dark => DarkValue,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme."),
    };

    private static bool IsSizeInRange(double size)
    {
        return size is >= UiSettings.MinimumSize and <= UiSettings.MaximumSize;
    }

    private UiSettings Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UiSettingsException($"Settings file '{DisplayFileName}' must contain a JSON object.");
        }

        UiSettings settings = UiSettings.Default;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seenKeys.Add(property.Name))
            {
                throw InvalidKey(property.Name, "is given more than once");
            }

            settings = property.Name switch
            {
                LayoutKey => settings with { Layout = ReadLayout(property) },
                ThemeKey => settings with { Theme = ReadTheme(property) },
                NavigationWidthKey => settings with { NavigationWidth = ReadSize(property) },
                ListSizeKey => settings with { ListSize = ReadSize(property) },
                _ => throw InvalidKey(property.Name, "is not a known setting"),
            };
        }

        return settings;
    }

    private ReadingPaneLayout ReadLayout(JsonProperty property)
    {
        return ReadString(property) switch
        {
            SideBySideValue => ReadingPaneLayout.SideBySide,
            ListAboveValue => ReadingPaneLayout.ListAbove,
            _ => throw InvalidKey(property.Name, "has an unknown value"),
        };
    }

    private AppTheme ReadTheme(JsonProperty property)
    {
        return ReadString(property) switch
        {
            SystemValue => AppTheme.System,
            LightValue => AppTheme.Light,
            DarkValue => AppTheme.Dark,
            _ => throw InvalidKey(property.Name, "has an unknown value"),
        };
    }

    private string ReadString(JsonProperty property)
    {
        return property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()!
            : throw InvalidKey(property.Name, "must be a string");
    }

    private double ReadSize(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out double size))
        {
            throw InvalidKey(property.Name, "must be a number");
        }

        return IsSizeInRange(size) ? size : throw OutOfRange(property.Name);
    }

    private void EnsureSizeInRange(double size, string key)
    {
        if (!IsSizeInRange(size))
        {
            throw OutOfRange(key);
        }
    }

    private UiSettingsException OutOfRange(string key)
    {
        return InvalidKey(key, $"must be between {UiSettings.MinimumSize:0} and {UiSettings.MaximumSize:0}");
    }

    // An unknown key is file content of arbitrary length, so key names are cleaned and truncated.
    private UiSettingsException InvalidKey(string key, string problem)
    {
        string displayKey = DisplayText.Truncate(SafeDisplayText.Clean(key));
        return new UiSettingsException($"Settings file '{DisplayFileName}': key '{displayKey}' {problem}.");
    }
}

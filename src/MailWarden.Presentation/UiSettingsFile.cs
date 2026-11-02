using System.Text.Json;
using MailWarden.Core.Classification;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Presentation;

/// <summary>
/// <see cref="IUiSettingsStore"/> on a JSON file. Strict on purpose: a typo in a key or a wrong type is reported, never
/// silently ignored. Keys: <c>layout</c> ("sideBySide", "listAbove"), <c>theme</c> ("system", "light", "dark", "nightshade"),
/// <c>navigationWidth</c>, <c>listWidth</c>, <c>listHeight</c> (numbers within the limits of <see cref="UiSettings"/>),
/// <c>markReadOnSelect</c>, <c>windowMaximized</c> (booleans), <c>windowWidth</c>, <c>windowHeight</c> (numbers),
/// <c>windowX</c>, <c>windowY</c> (whole numbers). A window value that is not stored is left out.
/// </summary>
public sealed class UiSettingsFile : IUiSettingsStore
{
    /// <summary>File name below the data root.</summary>
    public const string FileName = "ui-settings.json";

    private const string LayoutKey = "layout";
    private const string ThemeKey = "theme";
    private const string NavigationWidthKey = "navigationWidth";
    private const string ListWidthKey = "listWidth";
    private const string ListHeightKey = "listHeight";
    private const string MarkReadOnSelectKey = "markReadOnSelect";
    private const string WindowWidthKey = "windowWidth";
    private const string WindowHeightKey = "windowHeight";
    private const string WindowXKey = "windowX";
    private const string WindowYKey = "windowY";
    private const string WindowMaximizedKey = "windowMaximized";

    private const string SideBySideValue = "sideBySide";
    private const string ListAboveValue = "listAbove";
    private const string SystemValue = "system";
    private const string LightValue = "light";
    private const string DarkValue = "dark";
    private const string NightshadeValue = "nightshade";

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
        EnsureSizeInRange(settings.ListWidth, ListWidthKey);
        EnsureSizeInRange(settings.ListHeight, ListHeightKey);
        EnsureWindowSizeInRange(settings.WindowWidth, WindowWidthKey);
        EnsureWindowSizeInRange(settings.WindowHeight, WindowHeightKey);
        EnsureWindowPositionInRange(settings.WindowX, WindowXKey);
        EnsureWindowPositionInRange(settings.WindowY, WindowYKey);

        using var content = new MemoryStream();
        using (var writer = new Utf8JsonWriter(content, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString(LayoutKey, ToValue(settings.Layout));
            writer.WriteString(ThemeKey, ToValue(settings.Theme));
            writer.WriteNumber(NavigationWidthKey, settings.NavigationWidth);
            writer.WriteNumber(ListWidthKey, settings.ListWidth);
            writer.WriteNumber(ListHeightKey, settings.ListHeight);
            writer.WriteBoolean(MarkReadOnSelectKey, settings.MarkReadOnSelect);
            WriteIfStored(writer, WindowWidthKey, settings.WindowWidth);
            WriteIfStored(writer, WindowHeightKey, settings.WindowHeight);
            WriteIfStored(writer, WindowXKey, settings.WindowX);
            WriteIfStored(writer, WindowYKey, settings.WindowY);
            writer.WriteBoolean(WindowMaximizedKey, settings.WindowMaximized);
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllBytes(path, content.ToArray());
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
        AppTheme.Nightshade => NightshadeValue,
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown theme."),
    };

    private static void WriteIfStored(Utf8JsonWriter writer, string key, double? value)
    {
        if (value is double number)
        {
            writer.WriteNumber(key, number);
        }
    }

    private static void WriteIfStored(Utf8JsonWriter writer, string key, int? value)
    {
        if (value is int number)
        {
            writer.WriteNumber(key, number);
        }
    }

    private static bool IsWindowSizeInRange(double size)
    {
        return size is >= UiSettings.MinimumWindowSize and <= UiSettings.MaximumWindowSize;
    }

    private static bool IsWindowPositionInRange(int position)
    {
        return position is >= -UiSettings.MaximumWindowPosition and <= UiSettings.MaximumWindowPosition;
    }

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
                ListWidthKey => settings with { ListWidth = ReadSize(property) },
                ListHeightKey => settings with { ListHeight = ReadSize(property) },
                MarkReadOnSelectKey => settings with { MarkReadOnSelect = ReadBoolean(property) },
                WindowWidthKey => settings with { WindowWidth = ReadWindowSize(property) },
                WindowHeightKey => settings with { WindowHeight = ReadWindowSize(property) },
                WindowXKey => settings with { WindowX = ReadWindowPosition(property) },
                WindowYKey => settings with { WindowY = ReadWindowPosition(property) },
                WindowMaximizedKey => settings with { WindowMaximized = ReadBoolean(property) },
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
            NightshadeValue => AppTheme.Nightshade,
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

    private bool ReadBoolean(JsonProperty property)
    {
        return property.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidKey(property.Name, "must be true or false"),
        };
    }

    private double ReadWindowSize(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out double size))
        {
            throw InvalidKey(property.Name, "must be a number");
        }

        return IsWindowSizeInRange(size) ? size : throw WindowSizeOutOfRange(property.Name);
    }

    private int ReadWindowPosition(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int position))
        {
            throw InvalidKey(property.Name, "must be a whole number");
        }

        return IsWindowPositionInRange(position) ? position : throw WindowPositionOutOfRange(property.Name);
    }

    private void EnsureWindowSizeInRange(double? size, string key)
    {
        if (size is double value && !IsWindowSizeInRange(value))
        {
            throw WindowSizeOutOfRange(key);
        }
    }

    private void EnsureWindowPositionInRange(int? position, string key)
    {
        if (position is int value && !IsWindowPositionInRange(value))
        {
            throw WindowPositionOutOfRange(key);
        }
    }

    private UiSettingsException WindowSizeOutOfRange(string key)
    {
        return InvalidKey(key, $"must be between {UiSettings.MinimumWindowSize:0} and {UiSettings.MaximumWindowSize:0}");
    }

    private UiSettingsException WindowPositionOutOfRange(string key)
    {
        return InvalidKey(key, $"must be between {-UiSettings.MaximumWindowPosition} and {UiSettings.MaximumWindowPosition}");
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

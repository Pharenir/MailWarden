using System.Text;
using System.Text.Json;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a case 23 (D9): <see cref="UiSettingsFile"/>.</summary>
[Trait("Category", "feature:ui")]
public sealed class UiSettingsFileTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => Path.Combine(_temp.RootPath, UiSettingsFile.FileName);

    private UiSettings LoadFrom(string json)
    {
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        return new UiSettingsFile(SettingsPath).Load();
    }

    private UiSettingsException LoadFailureOf(string json)
    {
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        return Assert.Throws<UiSettingsException>(() => new UiSettingsFile(SettingsPath).Load());
    }

    // Case 23
    [Fact]
    public void File_name_constant_is_ui_settings_json()
    {
        Assert.Equal("ui-settings.json", UiSettingsFile.FileName);
    }

    // Case 23
    [Fact]
    public void Defaults_are_side_by_side_nightshade_280_and_420()
    {
        UiSettings defaults = UiSettings.Default;

        Assert.Equal(ReadingPaneLayout.SideBySide, defaults.Layout);
        Assert.Equal(AppTheme.Nightshade, defaults.Theme);
        Assert.Equal(280d, defaults.NavigationWidth);
        Assert.Equal(420d, defaults.ListWidth);
        Assert.Equal(320d, defaults.ListHeight);
    }

    // Case 23
    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = new UiSettingsFile(Path.Combine(_temp.RootPath, "absent", UiSettingsFile.FileName));

        Assert.Equal(UiSettings.Default, store.Load());
    }

    // Case 23
    [Fact]
    public void Empty_object_gives_defaults()
    {
        Assert.Equal(UiSettings.Default, LoadFrom("{}"));
    }

    // Case 23
    [Fact]
    public void Partial_file_keeps_defaults_for_missing_keys()
    {
        UiSettings loaded = LoadFrom("""{"layout": "listAbove"}""");

        Assert.Equal(ReadingPaneLayout.ListAbove, loaded.Layout);
        Assert.Equal(UiSettings.Default.Theme, loaded.Theme);
        Assert.Equal(UiSettings.Default.NavigationWidth, loaded.NavigationWidth);
        Assert.Equal(UiSettings.Default.ListWidth, loaded.ListWidth);
        Assert.Equal(UiSettings.Default.ListHeight, loaded.ListHeight);
    }

    // Case 23
    [Fact]
    public void Full_file_is_read_with_all_documented_values()
    {
        UiSettings loaded = LoadFrom("""{"layout": "listAbove", "theme": "dark", "navigationWidth": 333.5, "listWidth": 555, "listHeight": 444}""");

        Assert.Equal(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 333.5, 555, 444), loaded);
    }

    // Case 23
    [Theory]
    [InlineData("sideBySide", ReadingPaneLayout.SideBySide)]
    [InlineData("listAbove", ReadingPaneLayout.ListAbove)]
    public void Each_layout_value_is_read(string text, ReadingPaneLayout expected)
    {
        Assert.Equal(expected, LoadFrom($$"""{"layout": "{{text}}"}""").Layout);
    }

    // Case 23
    [Theory]
    [InlineData("system", AppTheme.System)]
    [InlineData("light", AppTheme.Light)]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("nightshade", AppTheme.Nightshade)]
    public void Each_theme_value_is_read(string text, AppTheme expected)
    {
        Assert.Equal(expected, LoadFrom($$"""{"theme": "{{text}}"}""").Theme);
    }

    // Case 23: the range 150-2000 includes both bounds.
    [Theory]
    [InlineData(150)]
    [InlineData(2000)]
    public void Boundary_sizes_are_accepted(int size)
    {
        UiSettings loaded = LoadFrom($$"""{"navigationWidth": {{size}}, "listWidth": {{size}}, "listHeight": {{size}}}""");

        Assert.Equal(size, loaded.NavigationWidth);
        Assert.Equal(size, loaded.ListWidth);
        Assert.Equal(size, loaded.ListHeight);
    }

    // Case 23
    [Fact]
    public void Round_trip_keeps_all_values()
    {
        var store = new UiSettingsFile(SettingsPath);
        var settings = new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Light, 301.25, 612.5, 287.5);

        store.Save(settings);

        Assert.Equal(settings, new UiSettingsFile(SettingsPath).Load());
    }

    // Case 23
    [Fact]
    public void Save_overwrites_an_existing_file()
    {
        var store = new UiSettingsFile(SettingsPath);
        store.Save(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 200, 300, 250));

        store.Save(new UiSettings(ReadingPaneLayout.SideBySide, AppTheme.Light, 400, 500, 350));

        Assert.Equal(new UiSettings(ReadingPaneLayout.SideBySide, AppTheme.Light, 400, 500, 350), store.Load());
    }

    // Case 23: documented keys and value spellings, UTF-8 without BOM, valid JSON, atomic write leaves no temp file.
    [Fact]
    public void Saved_file_uses_the_documented_keys_has_no_bom_and_leaves_no_temp_file()
    {
        var store = new UiSettingsFile(SettingsPath);

        store.Save(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 250, 450, 350));

        byte[] bytes = File.ReadAllBytes(SettingsPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM found");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Assert.Equal("listAbove", root.GetProperty("layout").GetString());
        Assert.Equal("dark", root.GetProperty("theme").GetString());
        Assert.Equal(250d, root.GetProperty("navigationWidth").GetDouble());
        Assert.Equal(450d, root.GetProperty("listWidth").GetDouble());
        Assert.Equal(350d, root.GetProperty("listHeight").GetDouble());
        Assert.False(root.TryGetProperty("listSize", out _), "old key listSize written");
        Assert.Equal(new[] { UiSettingsFile.FileName }, Directory.GetFiles(_temp.RootPath).Select(path => Path.GetFileName(path)!).ToArray());
    }

    // Case 23: the file name and the key are named, values never.
    [Theory]
    [InlineData("{not json", null)]
    [InlineData("", null)]
    [InlineData("[]", null)]
    [InlineData("""{"colour": "red"}""", "colour")]
    [InlineData("""{"layout": 5}""", "layout")]
    [InlineData("""{"layout": "diagonal_secret"}""", "layout")]
    [InlineData("""{"theme": true}""", "theme")]
    [InlineData("""{"theme": "neon_secret"}""", "theme")]
    [InlineData("""{"theme": "Nightshade"}""", "theme")]
    [InlineData("""{"theme": "frost"}""", "theme")]
    [InlineData("""{"theme": 3}""", "theme")]
    [InlineData("""{"navigationWidth": "wide_secret"}""", "navigationWidth")]
    [InlineData("""{"listWidth": "huge_secret"}""", "listWidth")]
    [InlineData("""{"listHeight": "huge_secret"}""", "listHeight")]
    [InlineData("""{"listSize": 4242}""", "listSize")]
    [InlineData("""{"navigationWidth": 149}""", "navigationWidth")]
    [InlineData("""{"navigationWidth": 2001}""", "navigationWidth")]
    [InlineData("""{"listWidth": 149}""", "listWidth")]
    [InlineData("""{"listHeight": 149}""", "listHeight")]
    [InlineData("""{"listWidth": 2001.5}""", "listWidth")]
    [InlineData("""{"listHeight": 2001.5}""", "listHeight")]
    [InlineData("""{"listWidth": -5}""", "listWidth")]
    [InlineData("""{"listHeight": -5}""", "listHeight")]
    public void Invalid_content_throws_UiSettingsException_naming_file_and_key_without_values(string json, string? key)
    {
        UiSettingsException exception = LoadFailureOf(json);

        Assert.Contains(UiSettingsFile.FileName, exception.Message, StringComparison.Ordinal);
        if (key is not null)
        {
            Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        }

        foreach (string value in new[] { "diagonal_secret", "neon_secret", "wide_secret", "huge_secret", "2001", "149", "-5", "4242" })
        {
            Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
        }
    }

    // Case 23: broken JSON text is never echoed.
    [Fact]
    public void Broken_json_does_not_leak_file_content_into_the_message()
    {
        UiSettingsException exception = LoadFailureOf("""{"layout": "marker_secret_text", """);

        Assert.Contains(UiSettingsFile.FileName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("marker_secret_text", exception.Message, StringComparison.Ordinal);
    }

    // Case 23 control: a valid file after an invalid one loads again (no sticky state).
    [Fact]
    public void Store_recovers_after_the_file_is_fixed()
    {
        var store = new UiSettingsFile(SettingsPath);
        File.WriteAllText(SettingsPath, "{broken", new UTF8Encoding(false));
        Assert.Throws<UiSettingsException>(store.Load);

        File.WriteAllText(SettingsPath, """{"theme": "light"}""", new UTF8Encoding(false));

        Assert.Equal(AppTheme.Light, store.Load().Theme);
    }

    // ---- AP-04e cases 1-5 (E1, E2) -------------------------------------------------------------------------

    // AP-04e case 1
    [Fact]
    public void Nightshade_is_the_default_theme()
    {
        Assert.Equal(AppTheme.Nightshade, UiSettings.Default.Theme);
    }

    // AP-04e case 2: no file.
    [Fact]
    public void Missing_file_gives_nightshade()
    {
        var store = new UiSettingsFile(Path.Combine(_temp.RootPath, "absent", UiSettingsFile.FileName));

        Assert.Equal(AppTheme.Nightshade, store.Load().Theme);
    }

    // AP-04e case 2: file without the key theme (also with other keys, also empty).
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"layout": "listAbove"}""")]
    [InlineData("""{"navigationWidth": 300, "listWidth": 500, "listHeight": 350}""")]
    [InlineData("""{"markReadOnSelect": true, "windowMaximized": true}""")]
    public void File_without_the_theme_key_gives_nightshade(string json)
    {
        Assert.Equal(AppTheme.Nightshade, LoadFrom(json).Theme);
    }

    // AP-04e case 3: a stored theme stays exactly as stored.
    [Theory]
    [InlineData("system", AppTheme.System)]
    [InlineData("light", AppTheme.Light)]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("nightshade", AppTheme.Nightshade)]
    public void A_stored_theme_stays_as_stored(string text, AppTheme expected)
    {
        UiSettings loaded = LoadFrom($$"""{"layout": "listAbove", "theme": "{{text}}", "listWidth": 500}""");

        Assert.Equal(expected, loaded.Theme);
        Assert.Equal(ReadingPaneLayout.ListAbove, loaded.Layout);
        Assert.Equal(500d, loaded.ListWidth);
    }

    // AP-04e case 4: save and load again, the file holds "nightshade".
    [Fact]
    public void Nightshade_round_trips_and_is_written_as_nightshade()
    {
        var store = new UiSettingsFile(SettingsPath);

        store.Save(UiSettings.Default with { Theme = AppTheme.Nightshade });

        Assert.Equal(AppTheme.Nightshade, new UiSettingsFile(SettingsPath).Load().Theme);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
        Assert.Equal("nightshade", document.RootElement.GetProperty("theme").GetString());
    }

    // AP-04e case 4: every theme is written with its own lower-case spelling.
    [Theory]
    [InlineData(AppTheme.System, "system")]
    [InlineData(AppTheme.Light, "light")]
    [InlineData(AppTheme.Dark, "dark")]
    [InlineData(AppTheme.Nightshade, "nightshade")]
    public void Every_theme_is_written_with_its_spelling(AppTheme theme, string expectedText)
    {
        new UiSettingsFile(SettingsPath).Save(UiSettings.Default with { Theme = theme });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
        Assert.Equal(expectedText, document.RootElement.GetProperty("theme").GetString());
        Assert.Equal(theme, new UiSettingsFile(SettingsPath).Load().Theme);
    }

    // AP-04e case 5: a rejected theme value names the key and never the value (the capital spelling is rejected too).
    [Theory]
    [InlineData("""{"theme": "Nightshade"}""")]
    [InlineData("""{"theme": "frost"}""")]
    [InlineData("""{"theme": 3}""")]
    [InlineData("""{"theme": "NIGHTSHADE"}""")]
    public void Unknown_theme_values_are_rejected_naming_the_key_but_not_the_value(string json)
    {
        UiSettingsException exception = LoadFailureOf(json);

        Assert.Contains("theme", exception.Message, StringComparison.Ordinal);
        Assert.Contains(UiSettingsFile.FileName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("frost", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nightshade", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NIGHTSHADE", exception.Message, StringComparison.Ordinal);
    }
}

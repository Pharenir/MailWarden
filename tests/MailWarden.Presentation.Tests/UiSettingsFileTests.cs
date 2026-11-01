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
    public void Defaults_are_side_by_side_system_280_and_420()
    {
        UiSettings defaults = UiSettings.Default;

        Assert.Equal(ReadingPaneLayout.SideBySide, defaults.Layout);
        Assert.Equal(AppTheme.System, defaults.Theme);
        Assert.Equal(280d, defaults.NavigationWidth);
        Assert.Equal(420d, defaults.ListSize);
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
        Assert.Equal(UiSettings.Default.ListSize, loaded.ListSize);
    }

    // Case 23
    [Fact]
    public void Full_file_is_read_with_all_documented_values()
    {
        UiSettings loaded = LoadFrom("""{"layout": "listAbove", "theme": "dark", "navigationWidth": 333.5, "listSize": 555}""");

        Assert.Equal(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 333.5, 555), loaded);
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
        UiSettings loaded = LoadFrom($$"""{"navigationWidth": {{size}}, "listSize": {{size}}}""");

        Assert.Equal(size, loaded.NavigationWidth);
        Assert.Equal(size, loaded.ListSize);
    }

    // Case 23
    [Fact]
    public void Round_trip_keeps_all_values()
    {
        var store = new UiSettingsFile(SettingsPath);
        var settings = new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Light, 301.25, 612.5);

        store.Save(settings);

        Assert.Equal(settings, new UiSettingsFile(SettingsPath).Load());
    }

    // Case 23
    [Fact]
    public void Save_overwrites_an_existing_file()
    {
        var store = new UiSettingsFile(SettingsPath);
        store.Save(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 200, 300));

        store.Save(new UiSettings(ReadingPaneLayout.SideBySide, AppTheme.Light, 400, 500));

        Assert.Equal(new UiSettings(ReadingPaneLayout.SideBySide, AppTheme.Light, 400, 500), store.Load());
    }

    // Case 23: documented keys and value spellings, UTF-8 without BOM, valid JSON, atomic write leaves no temp file.
    [Fact]
    public void Saved_file_uses_the_documented_keys_has_no_bom_and_leaves_no_temp_file()
    {
        var store = new UiSettingsFile(SettingsPath);

        store.Save(new UiSettings(ReadingPaneLayout.ListAbove, AppTheme.Dark, 250, 450));

        byte[] bytes = File.ReadAllBytes(SettingsPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM found");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Assert.Equal("listAbove", root.GetProperty("layout").GetString());
        Assert.Equal("dark", root.GetProperty("theme").GetString());
        Assert.Equal(250d, root.GetProperty("navigationWidth").GetDouble());
        Assert.Equal(450d, root.GetProperty("listSize").GetDouble());
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
    [InlineData("""{"navigationWidth": "wide_secret"}""", "navigationWidth")]
    [InlineData("""{"listSize": "huge_secret"}""", "listSize")]
    [InlineData("""{"navigationWidth": 149}""", "navigationWidth")]
    [InlineData("""{"navigationWidth": 2001}""", "navigationWidth")]
    [InlineData("""{"listSize": 149}""", "listSize")]
    [InlineData("""{"listSize": 2001.5}""", "listSize")]
    [InlineData("""{"listSize": -5}""", "listSize")]
    public void Invalid_content_throws_UiSettingsException_naming_file_and_key_without_values(string json, string? key)
    {
        UiSettingsException exception = LoadFailureOf(json);

        Assert.Contains(UiSettingsFile.FileName, exception.Message, StringComparison.Ordinal);
        if (key is not null)
        {
            Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        }

        foreach (string value in new[] { "diagonal_secret", "neon_secret", "wide_secret", "huge_secret", "2001", "149", "-5" })
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
}

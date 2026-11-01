using System.Text;
using System.Text.Json;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b D4 (case 18): the new keys of <see cref="UiSettingsFile"/> and the monitor check of the window position.</summary>
[Trait("Category", "feature:ui")]
public sealed class WindowSettingsTests : IDisposable
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

    // Case 18: defaults.
    [Fact]
    [Trait("Category", "smoke")]
    public void New_members_default_to_off_and_unknown()
    {
        UiSettings defaults = UiSettings.Default;

        Assert.False(defaults.MarkReadOnSelect);
        Assert.Null(defaults.WindowWidth);
        Assert.Null(defaults.WindowHeight);
        Assert.Null(defaults.WindowX);
        Assert.Null(defaults.WindowY);
        Assert.False(defaults.WindowMaximized);
    }

    // Case 18: a file of AP-04a without the new keys still loads with the defaults.
    [Fact]
    public void Missing_new_keys_give_the_defaults_and_keep_the_old_values()
    {
        UiSettings loaded = LoadFrom("""{"layout": "listAbove", "theme": "dark", "navigationWidth": 300, "listWidth": 500, "listHeight": 350}""");

        Assert.Equal(ReadingPaneLayout.ListAbove, loaded.Layout);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(300d, loaded.NavigationWidth);
        Assert.False(loaded.MarkReadOnSelect);
        Assert.Null(loaded.WindowWidth);
        Assert.Null(loaded.WindowHeight);
        Assert.Null(loaded.WindowX);
        Assert.Null(loaded.WindowY);
        Assert.False(loaded.WindowMaximized);
    }

    // Case 18: all new keys.
    [Fact]
    public void All_new_keys_are_read()
    {
        UiSettings loaded = LoadFrom("""{"markReadOnSelect": true, "windowWidth": 1280.5, "windowHeight": 800, "windowX": -150, "windowY": 40, "windowMaximized": true}""");

        Assert.True(loaded.MarkReadOnSelect);
        Assert.Equal(1280.5, loaded.WindowWidth);
        Assert.Equal(800d, loaded.WindowHeight);
        Assert.Equal(-150, loaded.WindowX);
        Assert.Equal(40, loaded.WindowY);
        Assert.True(loaded.WindowMaximized);
        Assert.Equal(UiSettings.Default.Layout, loaded.Layout);
    }

    // Case 18: each key on its own.
    [Theory]
    [InlineData("""{"markReadOnSelect": true}""")]
    [InlineData("""{"windowMaximized": true}""")]
    [InlineData("""{"windowWidth": 900}""")]
    [InlineData("""{"windowX": 10}""")]
    public void A_single_new_key_changes_only_its_own_member(string json)
    {
        UiSettings loaded = LoadFrom(json);

        Assert.NotEqual(UiSettings.Default, loaded);
        Assert.Equal(UiSettings.Default.Layout, loaded.Layout);
        Assert.Equal(UiSettings.Default.ListWidth, loaded.ListWidth);
    }

    // Case 18: the range includes both bounds.
    [Theory]
    [InlineData("windowWidth", 400)]
    [InlineData("windowWidth", 20000)]
    [InlineData("windowHeight", 400)]
    [InlineData("windowHeight", 20000)]
    [InlineData("windowX", -20000)]
    [InlineData("windowX", 20000)]
    [InlineData("windowY", -20000)]
    [InlineData("windowY", 20000)]
    public void Boundary_values_are_accepted(string key, int value)
    {
        UiSettings loaded = LoadFrom($$"""{"{{key}}": {{value}}}""");

        double actual = key switch
        {
            "windowWidth" => loaded.WindowWidth!.Value,
            "windowHeight" => loaded.WindowHeight!.Value,
            "windowX" => loaded.WindowX!.Value,
            _ => loaded.WindowY!.Value,
        };
        Assert.Equal(value, actual);
    }

    // Case 18: out of range or wrong type names file and key, never the value.
    [Theory]
    [InlineData("""{"windowWidth": 399}""", "windowWidth")]
    [InlineData("""{"windowWidth": 20001}""", "windowWidth")]
    [InlineData("""{"windowHeight": 399}""", "windowHeight")]
    [InlineData("""{"windowHeight": 20001}""", "windowHeight")]
    [InlineData("""{"windowWidth": -5}""", "windowWidth")]
    [InlineData("""{"windowX": -20001}""", "windowX")]
    [InlineData("""{"windowX": 20001}""", "windowX")]
    [InlineData("""{"windowY": -20001}""", "windowY")]
    [InlineData("""{"windowY": 20001}""", "windowY")]
    [InlineData("""{"windowX": 1.5}""", "windowX")]
    [InlineData("""{"windowWidth": "wide_secret"}""", "windowWidth")]
    [InlineData("""{"windowHeight": "tall_secret"}""", "windowHeight")]
    [InlineData("""{"windowX": "left_secret"}""", "windowX")]
    [InlineData("""{"windowY": "top_secret"}""", "windowY")]
    [InlineData("""{"markReadOnSelect": "yes_secret"}""", "markReadOnSelect")]
    [InlineData("""{"markReadOnSelect": 1}""", "markReadOnSelect")]
    [InlineData("""{"windowMaximized": "maybe_secret"}""", "windowMaximized")]
    [InlineData("""{"windowMaximized": 0}""", "windowMaximized")]
    public void Invalid_new_values_throw_UiSettingsException_naming_file_and_key_without_values(string json, string key)
    {
        UiSettingsException exception = LoadFailureOf(json);

        Assert.Contains(UiSettingsFile.FileName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        foreach (string value in new[] { "wide_secret", "tall_secret", "left_secret", "top_secret", "yes_secret", "maybe_secret", "20001", "-20001", "399" })
        {
            Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
        }
    }

    // Case 18: null is not a number; the loader fails fast and names the key (builder's reading, fail-fast rule).
    [Theory]
    [InlineData("""{"windowWidth": null}""", "windowWidth")]
    [InlineData("""{"windowHeight": null}""", "windowHeight")]
    [InlineData("""{"windowX": null}""", "windowX")]
    [InlineData("""{"windowY": null}""", "windowY")]
    public void Null_window_values_are_rejected_naming_the_key(string json, string key)
    {
        UiSettingsException exception = LoadFailureOf(json);

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    // Case 18: round trip with every value, then with unknown window values.
    [Fact]
    public void Round_trip_keeps_all_values_including_the_new_ones()
    {
        var store = new UiSettingsFile(SettingsPath);
        UiSettings settings = UiSettings.Default with
        {
            Layout = ReadingPaneLayout.ListAbove,
            Theme = AppTheme.Light,
            MarkReadOnSelect = true,
            WindowWidth = 1234.5,
            WindowHeight = 765,
            WindowX = -20,
            WindowY = 33,
            WindowMaximized = true,
        };

        store.Save(settings);

        Assert.Equal(settings, new UiSettingsFile(SettingsPath).Load());
    }

    // Case 18
    [Fact]
    public void Round_trip_keeps_unknown_window_values_unknown()
    {
        var store = new UiSettingsFile(SettingsPath);
        UiSettings settings = UiSettings.Default with { MarkReadOnSelect = true };

        store.Save(settings);

        UiSettings loaded = new UiSettingsFile(SettingsPath).Load();
        Assert.Equal(settings, loaded);
        Assert.Null(loaded.WindowWidth);
        Assert.Null(loaded.WindowX);
    }

    // Case 18: documented keys and types in the saved file.
    [Fact]
    public void Saved_file_uses_the_documented_new_keys()
    {
        var store = new UiSettingsFile(SettingsPath);

        store.Save(UiSettings.Default with { MarkReadOnSelect = true, WindowWidth = 1000, WindowHeight = 700, WindowX = -5, WindowY = 6, WindowMaximized = true });

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(SettingsPath));
        JsonElement root = document.RootElement;
        Assert.True(root.GetProperty("markReadOnSelect").GetBoolean());
        Assert.Equal(1000d, root.GetProperty("windowWidth").GetDouble());
        Assert.Equal(700d, root.GetProperty("windowHeight").GetDouble());
        Assert.Equal(-5, root.GetProperty("windowX").GetInt32());
        Assert.Equal(6, root.GetProperty("windowY").GetInt32());
        Assert.True(root.GetProperty("windowMaximized").GetBoolean());
        Assert.Equal(new[] { UiSettingsFile.FileName }, Directory.GetFiles(_temp.RootPath).Select(path => Path.GetFileName(path)!).ToArray());
    }

    // Case 18: the shell keeps the window values when it saves something else (it must not wipe what App stored).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Layout_change_in_the_shell_keeps_the_window_values()
    {
        UiSettings initial = UiSettings.Default with { WindowWidth = 1111, WindowHeight = 777, WindowX = 12, WindowY = 34, WindowMaximized = true, MarkReadOnSelect = true };
        var harness = new ShellHarness(initial);
        await harness.Shell.InitializeAsync();

        harness.Shell.Layout = ReadingPaneLayout.ListAbove;

        Assert.Equal(initial with { Layout = ReadingPaneLayout.ListAbove }, harness.Settings.Saved.Last());
    }

    // ---- Position off every monitor (pure helper) ----------------------------------------------------------
    // The helper's name and signature are not in the contract; see test-plan question Q4.

    private static readonly IReadOnlyList<(double X, double Y, double Width, double Height)> TwoMonitors =
    [
        (0, 0, 1920, 1080),
        (-1280, 0, 1280, 1024),
    ];

    // Case 18
    [Theory]
    [InlineData(100, 100, 1000, 700)]
    [InlineData(-1200, 50, 800, 600)]
    [InlineData(0, 0, 1920, 1080)]
    public void A_window_on_a_monitor_is_kept(int x, int y, double width, double height)
    {
        Assert.True(WindowPlacement.IsOnAnyMonitor(x, y, width, height, TwoMonitors));
    }

    // Case 18
    [Theory]
    [InlineData(5000, 100, 1000, 700)]
    [InlineData(-5000, 0, 800, 600)]
    [InlineData(100, 4000, 800, 600)]
    [InlineData(100, -4000, 800, 600)]
    public void A_window_off_every_monitor_is_dropped(int x, int y, double width, double height)
    {
        Assert.False(WindowPlacement.IsOnAnyMonitor(x, y, width, height, TwoMonitors));
    }

    // Case 18: a window that is only partly on a monitor but whose top-left corner is on it counts as visible.
    [Fact]
    public void A_window_with_its_top_left_corner_on_a_monitor_is_visible()
    {
        Assert.True(WindowPlacement.IsOnAnyMonitor(1500, 800, 1200, 800, TwoMonitors));
    }

    // Case 18: no monitor information means nothing can be kept.
    [Fact]
    public void Without_any_monitor_the_position_is_dropped()
    {
        Assert.False(WindowPlacement.IsOnAnyMonitor(0, 0, 800, 600, Array.Empty<(double X, double Y, double Width, double Height)>()));
    }
}

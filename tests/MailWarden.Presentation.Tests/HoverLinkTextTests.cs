using System.ComponentModel;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b D6 (case 19): <see cref="ReadingPaneViewModel.HoverLinkText"/>, the cleaned link target for the tooltip.</summary>
[Trait("Category", "feature:ui")]
public sealed class HoverLinkTextTests
{
    private const int MaxLength = 501;

    private static ReadingPaneViewModel NewReading() => new ShellHarness().Shell.Reading;

    // Case 19: set
    [Fact]
    [Trait("Category", "smoke")]
    public void A_link_target_is_shown_as_it_is()
    {
        ReadingPaneViewModel reading = NewReading();

        reading.HoverLinkText = "https://www.example.test/path?q=1";

        Assert.Equal("https://www.example.test/path?q=1", reading.HoverLinkText);
    }

    // Case 19: cleared
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clearing_the_target_empties_the_text(string? cleared)
    {
        ReadingPaneViewModel reading = NewReading();
        reading.HoverLinkText = "https://www.example.test/";

        reading.HoverLinkText = cleared;

        Assert.True(string.IsNullOrEmpty(reading.HoverLinkText));
    }

    // Case 19: only whitespace or control characters is nothing to show.
    [Theory]
    [InlineData("   ")]
    [InlineData("\u0007\u0008")]
    public void A_target_without_visible_characters_shows_nothing(string raw)
    {
        ReadingPaneViewModel reading = NewReading();

        reading.HoverLinkText = raw;

        Assert.True(string.IsNullOrWhiteSpace(reading.HoverLinkText));
    }

    // Case 19: bidi override and control characters cannot disguise the target.
    [Fact]
    public void Bidi_and_control_characters_are_removed()
    {
        ReadingPaneViewModel reading = NewReading();

        reading.HoverLinkText = "https://www.example.test/‮fdp.exe⁦\u0007\r\nnext";

        string shown = reading.HoverLinkText ?? string.Empty;
        Assert.DoesNotContain('‮', shown);
        Assert.DoesNotContain('⁦', shown);
        Assert.DoesNotContain('\u0007', shown);
        Assert.DoesNotContain('\r', shown);
        Assert.DoesNotContain('\n', shown);
        Assert.StartsWith("https://www.example.test/", shown, StringComparison.Ordinal);
    }

    // Case 19 / 29: a non-URL text of 10,000 characters is shortened to at most 501 characters.
    [Fact]
    public void A_10000_character_text_is_shortened_to_at_most_501_characters()
    {
        ReadingPaneViewModel reading = NewReading();

        reading.HoverLinkText = new string('a', 10_000);

        string shown = reading.HoverLinkText ?? string.Empty;
        Assert.True(shown.Length <= MaxLength, $"length was {shown.Length}");
        Assert.True(shown.Length > 100);
    }

    // Case 19 / 29 (U5): a long absolute target shows the host first, then the cut text; the length stays bounded.
    [Fact]
    public void A_long_absolute_target_shows_the_host_first_and_stays_bounded()
    {
        ReadingPaneViewModel reading = NewReading();
        string target = "https://www.example.test/" + new string('a', 10_000 - 25);

        reading.HoverLinkText = target;

        string shown = reading.HoverLinkText ?? string.Empty;
        Assert.StartsWith("www.example.test", shown, StringComparison.Ordinal);
        Assert.Contains("aaaa", shown, StringComparison.Ordinal);
        Assert.True(shown.Length <= MaxLength + 100, $"length was {shown.Length}");
        Assert.True(shown.Length > 100);
    }

    // Case 29 (U5): user info cannot put a foreign host in front; the real host (after the @) comes first.
    [Fact]
    public void User_info_cannot_put_a_foreign_host_in_front_of_the_real_host()
    {
        ReadingPaneViewModel reading = NewReading();
        string target = "https://trusted.example.test:pw@evil.example.test/" + new string('b', 2_000);

        reading.HoverLinkText = target;

        string shown = reading.HoverLinkText ?? string.Empty;
        Assert.StartsWith("evil.example.test", shown, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"^\W*trusted\.example\.test", shown);
    }

    // Case 29 positive control: a short target with user info is not rewritten into something that hides the real host.
    [Fact]
    public void A_short_target_with_user_info_still_contains_the_real_host()
    {
        ReadingPaneViewModel reading = NewReading();

        reading.HoverLinkText = "https://trusted.example.test@evil.example.test/x";

        Assert.Contains("evil.example.test", reading.HoverLinkText ?? string.Empty, StringComparison.Ordinal);
    }

    // Case 19: a target of exactly 500 characters is not touched.
    [Fact]
    public void A_500_character_target_is_kept_whole()
    {
        ReadingPaneViewModel reading = NewReading();
        string target = "https://www.example.test/" + new string('b', 475);
        Assert.Equal(500, target.Length);

        reading.HoverLinkText = target;

        Assert.Equal(target, reading.HoverLinkText);
    }

    // Case 19: the host is told about changes (the tooltip follows the property).
    [Fact]
    public void Changing_the_text_raises_property_changed()
    {
        ReadingPaneViewModel reading = NewReading();
        List<string?> changed = [];
        ((INotifyPropertyChanged)reading).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        reading.HoverLinkText = "https://www.example.test/";
        reading.HoverLinkText = null;

        Assert.Equal(2, changed.Count(name => name == nameof(ReadingPaneViewModel.HoverLinkText)));
    }
}

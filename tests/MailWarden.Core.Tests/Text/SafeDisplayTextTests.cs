using MailWarden.Core.Text;

namespace MailWarden.Core.Tests.Text;

/// <summary>AP-04a case 1 (D1): <see cref="SafeDisplayText.Clean"/>.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "smoke")]
public sealed class SafeDisplayTextTests
{
    // Pure, synchronous logic: no per-test timeout needed.

    // Case 1
    [Fact]
    public void Plain_letters_digits_and_punctuation_are_unchanged()
    {
        Assert.Equal("Invoice 2026: Sender_A, ok!", SafeDisplayText.Clean("Invoice 2026: Sender_A, ok!"));
    }

    // Case 1
    [Fact]
    public void Empty_string_stays_empty()
    {
        Assert.Equal(string.Empty, SafeDisplayText.Clean(string.Empty));
    }

    // Case 1
    [Fact]
    public void Emoji_and_non_ascii_letters_are_unchanged()
    {
        const string text = "Grüße \U0001F600 日本語";

        Assert.Equal(text, SafeDisplayText.Clean(text));
    }

    // Case 1
    [Theory]
    [InlineData('\u202E')] // RLO
    [InlineData('\u202A')]
    [InlineData('\u202B')]
    [InlineData('\u202C')]
    [InlineData('\u202D')]
    [InlineData('\u2066')] // LRI
    [InlineData('\u2067')]
    [InlineData('\u2068')]
    [InlineData('\u2069')]
    [InlineData('\u200E')]
    [InlineData('\u200F')]
    [InlineData('\u061C')]
    public void Each_bidi_control_is_removed(char control)
    {
        Assert.Equal("abcd", SafeDisplayText.Clean($"ab{control}cd"));
    }

    // Case 1
    [Fact]
    public void Rtlo_spoofing_of_a_file_extension_loses_the_control_but_keeps_the_letters()
    {
        Assert.Equal("invoicecod.exe", SafeDisplayText.Clean("invoice\u202Ecod.exe"));
    }

    // Case 1
    [Theory]
    [InlineData("a\rb")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    public void Cr_lf_and_tab_each_become_one_space(string input)
    {
        Assert.Equal("a b", SafeDisplayText.Clean(input));
    }

    // Case 1
    [Fact]
    public void Crlf_becomes_two_spaces_one_per_character_and_nothing_is_trimmed()
    {
        Assert.Equal(" a  b ", SafeDisplayText.Clean("\ta\r\nb\n"));
    }

    // Case 1
    [Theory]
    [InlineData('\u0007')] // BEL
    [InlineData('\u0000')]
    [InlineData('\u001B')] // ESC
    [InlineData('\u007F')] // DEL (Cc)
    [InlineData('\u0085')] // NEL (Cc)
    [InlineData('\u009F')] // C1 control
    public void Other_control_characters_are_removed(char control)
    {
        Assert.Equal("xy", SafeDisplayText.Clean($"x{control}y"));
    }

    // Case 1: positive control around the ranges; neighbours of the removed ranges stay.
    [Theory]
    [InlineData('\u2029')] // paragraph separator is not Cc and not in the removed list
    [InlineData('\u2065')]
    [InlineData('\u202F')] // narrow no-break space, right after U+202E
    [InlineData('\u200D')] // zero width joiner (emoji sequences)
    [InlineData('\u00A0')]
    public void Neighbours_of_the_removed_ranges_stay(char kept)
    {
        Assert.Equal($"a{kept}b", SafeDisplayText.Clean($"a{kept}b"));
    }

    // Case 1
    [Fact]
    public void Mixed_input_combines_all_rules()
    {
        Assert.Equal("Re: pay now \U0001F600 ok", SafeDisplayText.Clean("Re:\u202E pay\tnow \U0001F600\u0007\u2066 ok"));
    }

    // Case 1
    [Fact]
    public void Null_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SafeDisplayText.Clean(null!));
    }
}

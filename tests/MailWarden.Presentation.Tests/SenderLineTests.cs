namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04a case 75 (L6): the sender line of the reading pane (<c>MailDisplayText.SenderLine</c>) is checked through
/// <see cref="ReadingPaneViewModel.From"/>, with content produced by the real reader from generated From headers.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class SenderLineTests
{
    private static readonly string Ellipsis = ((char)0x2026).ToString();

    private static async Task<string> FromOfAsync(params (string Name, string Address)[] from)
    {
        var harness = new ShellHarness();
        (_, MailFolder inbox) = harness.SeedInbox("Account_A", 0);
        MailMessage message = harness.ReadModel.AddMessage(inbox);
        harness.ReadModel.SetContent(message.Id, TestContent.Plain("sender_line_subject", "body", from: from));

        await harness.Shell.Reading.ShowAsync(message);

        Assert.Equal(ReadingState.Ready, harness.Shell.Reading.State);
        return harness.Shell.Reading.From;
    }

    private static string WithoutClosingBracket(string line) => line.TrimEnd().TrimEnd('>').TrimEnd();

    // Case 75 positive control: an ordinary sender is shown with name and address and nothing else.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Ordinary_sender_is_shown_with_name_and_address()
    {
        string line = await FromOfAsync(("Sender A", "sender_a@example.test"));

        Assert.Contains("Sender A", line, StringComparison.Ordinal);
        Assert.Contains("sender_a@example.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain("weitere", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Ellipsis, line, StringComparison.Ordinal);
    }

    // Case 75 positive control: a sender without a name shows just the address.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sender_without_a_name_shows_the_address()
    {
        string line = await FromOfAsync((string.Empty, "sender_a@example.test"));

        Assert.Contains("sender_a@example.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain("weitere", line, StringComparison.Ordinal);
    }

    // Case 75
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Long_name_is_cut_at_100_and_the_address_stays_complete()
    {
        string line = await FromOfAsync((new string('L', 300), "sender_a@example.test"));

        Assert.Contains("sender_a@example.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('L', 101), line, StringComparison.Ordinal);
        Assert.Contains(new string('L', 50), line, StringComparison.Ordinal);
        Assert.Contains(Ellipsis, line, StringComparison.Ordinal);
        Assert.True(line.Length < 200, $"line has {line.Length} characters");
    }

    // Case 75 positive control: a name of exactly 100 characters is not cut.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Name_of_exactly_100_characters_is_not_cut()
    {
        string line = await FromOfAsync((new string('L', 100), "sender_a@example.test"));

        Assert.True(line.Contains(new string('L', 100), StringComparison.Ordinal), $"line has {line.Length} characters: [{line}]");
        Assert.DoesNotContain(Ellipsis, line, StringComparison.Ordinal);
    }

    // Case 75
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Two_mailboxes_show_only_the_first_and_count_the_rest()
    {
        string line = await FromOfAsync(("Alice", "alice@example.test"), ("Bob Boss", "bob.boss@other.test"));

        Assert.Contains("Alice", line, StringComparison.Ordinal);
        Assert.Contains("alice@example.test", line, StringComparison.Ordinal);
        Assert.Contains("+1 weitere", line, StringComparison.Ordinal);
        Assert.DoesNotContain("bob.boss@other.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain("other.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Bob", line, StringComparison.Ordinal);
    }

    // Case 75
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Many_mailboxes_count_all_the_others()
    {
        (string, string)[] senders = [.. Enumerable.Range(0, 1_000).Select(index => ($"Sender {index}", $"sender_{index}@example.test"))];

        string line = await FromOfAsync(senders);

        Assert.Contains("sender_0@example.test", line, StringComparison.Ordinal);
        Assert.Contains("+999 weitere", line, StringComparison.Ordinal);
        Assert.DoesNotContain("sender_1@example.test", line, StringComparison.Ordinal);
        Assert.True(line.Length < 300, $"line has {line.Length} characters");
    }

    // Case 75: a long name first, then an address only - the second address must never stand in for the sender.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Long_name_then_address_only_shows_the_first_mailbox_cut_and_counts_the_second()
    {
        string line = await FromOfAsync((new string('L', 300), "first@example.test"), (string.Empty, "second@other.test"));

        Assert.Contains("first@example.test", line, StringComparison.Ordinal);
        Assert.Contains("+1 weitere", line, StringComparison.Ordinal);
        Assert.DoesNotContain("second@other.test", line, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('L', 101), line, StringComparison.Ordinal);
    }

    // Case 75: a name that looks like an address must not hide the real one.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Name_with_a_fake_address_shows_the_real_address_at_the_end()
    {
        string line = await FromOfAsync(("Trusted Bank <a@b>", "real.sender@evil.test"));

        Assert.EndsWith("real.sender@evil.test", WithoutClosingBracket(line), StringComparison.Ordinal);
    }

    // Case 75: the same with a long real address: the domain stays visible at the end.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Name_with_a_fake_address_and_a_long_real_address_keeps_the_real_domain_at_the_end()
    {
        string line = await FromOfAsync(("Trusted Bank <a@b>", new string('x', 200) + "@evil.test"));

        Assert.EndsWith("@evil.test", WithoutClosingBracket(line), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 150), line, StringComparison.Ordinal);
        Assert.Contains(Ellipsis, line, StringComparison.Ordinal);
    }

    // Case 75: a name that itself is a long fake address with another domain.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Name_that_is_a_fake_address_with_a_trusted_domain_does_not_end_the_line()
    {
        string line = await FromOfAsync(("ceo@trusted-bank.test " + new string('y', 150), "real.sender@evil.test"));

        Assert.EndsWith("real.sender@evil.test", WithoutClosingBracket(line), StringComparison.Ordinal);
    }

    // Case 75
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Long_address_without_a_name_is_cut_in_the_middle_and_keeps_the_domain()
    {
        string line = await FromOfAsync((string.Empty, new string('a', 150) + "@evil.test"));

        Assert.Contains(Ellipsis + "@evil.test", line, StringComparison.Ordinal);
        Assert.EndsWith("@evil.test", WithoutClosingBracket(line), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 120), line, StringComparison.Ordinal);
        Assert.StartsWith("aaa", line.TrimStart('<'), StringComparison.Ordinal);
        Assert.True(line.Length < 150, $"line has {line.Length} characters");
    }

    // Case 75: the line is bounded whatever is thrown at it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Huge_name_and_huge_address_together_stay_short_and_keep_the_domain()
    {
        string line = await FromOfAsync((new string('N', 3_000), new string('z', 900) + "@evil.test"), ("Other", "other@other.test"));

        Assert.True(line.Length < 400, $"line has {line.Length} characters");
        Assert.Contains("@evil.test", line, StringComparison.Ordinal);
        Assert.Contains("+1 weitere", line, StringComparison.Ordinal);
        Assert.DoesNotContain("other@other.test", line, StringComparison.Ordinal);
    }
}

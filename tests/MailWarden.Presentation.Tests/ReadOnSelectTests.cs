using static MailWarden.Presentation.Tests.TestSupport.ActionsTestHelpers;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b D2 (cases 13 and 14): the "mark as read when selected" switch and its 1.5 s rule.</summary>
[Trait("Category", "feature:ui")]
public sealed class ReadOnSelectTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Gives a timer started by the shell a moment (real time) to be created before the manual clock moves.</summary>
    private static Task LetTimersArmAsync() => Task.Delay(100);

    private static async Task<(ShellHarness Harness, ManualTimeProvider Time)> InitializedWithSwitchAsync(bool switchOn, int count = 3, MessageFlags flags = MessageFlags.None)
    {
        var time = new ManualTimeProvider(Start);
        (ShellHarness harness, _, _, _) = await InitializedAsync(count, flags, clock: time);
        harness.Shell.MarkReadOnSelect = switchOn;
        return (harness, time);
    }

    // ---- Case 13 -------------------------------------------------------------------------------------------

    // Case 13: the default is off.
    [Fact]
    public void The_switch_defaults_to_off_in_the_settings_and_in_the_shell()
    {
        Assert.False(UiSettings.Default.MarkReadOnSelect);
        Assert.False(new ShellHarness().Shell.MarkReadOnSelect);
    }

    // Case 13: switched off, nothing is ever sent, however long a mail is shown.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task With_the_switch_off_selecting_and_waiting_never_sends_anything()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: false);
        MessageRowViewModel[] rows = [.. harness.Shell.Messages.Items];

        foreach (MessageRowViewModel row in rows)
        {
            await SelectAsync(harness, row);
            await LetTimersArmAsync();
            time.Advance(TimeSpan.FromMinutes(10));
            await Wait.NegativeWindowAsync();
        }

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Controller.Calls);
        Assert.All(harness.Shell.Messages.Items, row => Assert.True(row.IsUnread));
    }

    // Case 13: the stored value is applied at start.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_stored_switch_is_applied_after_initialisation()
    {
        var harness = new ShellHarness(UiSettings.Default with { MarkReadOnSelect = true });

        await harness.Shell.InitializeAsync();

        Assert.True(harness.Shell.MarkReadOnSelect);
    }

    // Case 13: changing the switch is saved and keeps the other values.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Changing_the_switch_saves_it_and_keeps_the_other_settings()
    {
        UiSettings initial = UiSettings.Default with { WindowWidth = 1111, WindowHeight = 777, WindowX = 12, WindowY = 34, WindowMaximized = true };
        var harness = new ShellHarness(initial);
        await harness.Shell.InitializeAsync();

        harness.Shell.MarkReadOnSelect = true;

        UiSettings saved = harness.Settings.Saved.Last();
        Assert.Equal(initial with { MarkReadOnSelect = true }, saved);
        Assert.True(harness.Shell.MarkReadOnSelect);
    }

    // ---- Case 14 -------------------------------------------------------------------------------------------

    // Case 14: 1.4 s and then leaving marks nothing, also not later.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Leaving_after_1_4_seconds_marks_nothing()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        await LetTimersArmAsync();

        time.Advance(TimeSpan.FromSeconds(1.4));
        harness.Shell.Messages.SelectedRows = [];
        await Wait.UntilAsync(() => harness.Shell.Reading.State == ReadingState.Empty, "left the mail");
        time.Advance(TimeSpan.FromMinutes(5));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
        Assert.True(harness.Shell.Messages.Items[0].IsUnread);
    }

    // Case 14: leaving for another mail also cancels the first one's timer; the second needs its own 1.5 s.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Switching_to_another_mail_restarts_the_clock()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true);
        MessageRowViewModel first = harness.Shell.Messages.Items[0];
        MessageRowViewModel second = harness.Shell.Messages.Items[1];
        await SelectAsync(harness, first);
        await LetTimersArmAsync();
        time.Advance(TimeSpan.FromSeconds(1.4));

        await SelectAsync(harness, second);
        await LetTimersArmAsync();
        time.Advance(TimeSpan.FromSeconds(1.4));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);

        time.Advance(TimeSpan.FromSeconds(0.2));
        await WaitForCallsAsync(harness, "SetSeen");
        ActionCall call = Assert.Single(harness.Actions.CallsNamed("SetSeen"));
        Assert.Equal(new[] { second.Message.Id }, call.MessageIds.ToArray());
    }

    // Case 14: 1.6 s marks exactly that mail, exactly once.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Showing_a_mail_for_1_6_seconds_marks_it_read_once()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true);
        MessageRowViewModel row = harness.Shell.Messages.Items[0];
        await SelectAsync(harness, row);
        await LetTimersArmAsync();

        time.Advance(TimeSpan.FromSeconds(1.6));

        await WaitForCallsAsync(harness, "SetSeen");
        await Wait.NegativeWindowAsync();
        time.Advance(TimeSpan.FromMinutes(5));
        await Wait.NegativeWindowAsync();
        ActionCall call = Assert.Single(harness.Actions.Calls);
        Assert.Equal("SetSeen", call.Name);
        Assert.True(call.Value);
        Assert.Equal(new[] { row.Message.Id }, call.MessageIds.ToArray());
        await Wait.UntilAsync(() => !row.IsUnread, "row shows read at once");
    }

    // Case 14: a mail that is already read is left alone.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_already_read_mail_is_not_marked_again()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true, flags: MessageFlags.Seen);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        await LetTimersArmAsync();

        time.Advance(TimeSpan.FromMinutes(5));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
    }

    // Case 14: a multi-selection shows no mail, so nothing is marked.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_multi_selection_marks_nothing_on_its_own()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true);
        harness.Shell.Messages.SelectedRows = [.. harness.Shell.Messages.Items.Take(2)];
        await LetTimersArmAsync();

        time.Advance(TimeSpan.FromMinutes(5));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
    }

    // Case 14: switching the switch off while the clock runs cancels the marking.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Switching_off_during_the_wait_marks_nothing()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        await LetTimersArmAsync();
        time.Advance(TimeSpan.FromSeconds(1.0));

        harness.Shell.MarkReadOnSelect = false;
        time.Advance(TimeSpan.FromMinutes(5));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
    }

    // Case 14 / 20: nothing fires after Dispose.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Dispose_cancels_a_running_wait()
    {
        (ShellHarness harness, ManualTimeProvider time) = await InitializedWithSwitchAsync(switchOn: true);
        await SelectAsync(harness, harness.Shell.Messages.Items[0]);
        await LetTimersArmAsync();

        harness.Shell.Dispose();
        time.Advance(TimeSpan.FromMinutes(5));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.Actions.Calls);
        Assert.Empty(harness.Log.Entries);
    }
}

using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-07d D4 / cases 14-16: the classification banner of <see cref="ShellViewModel"/>.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:classifier")]
public sealed class ClassificationBannerTests
{
    private const string MarkerSecret = "marker_secret_text";

    private static async Task<(ShellHarness Harness, Account AccountA, Account AccountB)> InitializedAsync()
    {
        var harness = new ShellHarness();
        (Account accountA, _) = harness.SeedInbox("Account_A", 1);
        (Account accountB, _) = harness.SeedInbox("Account_B", 1);
        harness.SetStatuses(
            FakeMailboxController.Status(accountA.Id, AccountSyncState.Idle),
            FakeMailboxController.Status(accountB.Id, AccountSyncState.Idle));
        await harness.Shell.InitializeAsync();
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 1 || harness.Shell.Messages.Items.Count == 2, "initial list");
        await Wait.NegativeWindowAsync();
        return (harness, accountA, accountB);
    }

    private static ClassificationPassSummary Summary(
        Guid accountId, int evaluationFailed = 0, int readFailed = 0, int brakeHeld = 0, bool actSuspended = false) =>
        new(accountId, evaluationFailed, readFailed, brakeHeld, actSuspended);

    // Case 14 (step 1): one account with failed evaluations shows the fixed text with the count.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Failed_evaluations_show_a_banner_with_the_count()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();
        Assert.Null(harness.Shell.Banner);

        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7));

        Assert.False(string.IsNullOrWhiteSpace(harness.Shell.Banner));
        Assert.Contains("7", harness.Shell.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("Account_A", harness.Shell.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 14 / 21 (fix.2): the sums since app start stay when a pass is clean; a second account adds its own kind.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Sums_stay_after_a_clean_pass_and_grow_with_other_accounts()
    {
        (ShellHarness harness, Account accountA, Account accountB) = await InitializedAsync();
        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7));
        string? onlyFailed = harness.Shell.Banner;
        Assert.Contains("7", onlyFailed, StringComparison.Ordinal);

        harness.Controller.RaiseClassification(Summary(accountA.Id));

        Assert.Equal(onlyFailed, harness.Shell.Banner);

        harness.Controller.RaiseClassification(Summary(accountB.Id, brakeHeld: 4));

        string? both = harness.Shell.Banner;
        Assert.NotNull(both);
        Assert.Contains("7", both, StringComparison.Ordinal);
        Assert.Contains("4", both, StringComparison.Ordinal);

        harness.Controller.RaiseClassification(Summary(accountB.Id));
        harness.Controller.RaiseClassification(Summary(accountA.Id));

        Assert.Equal(both, harness.Shell.Banner);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 21: a clean pass alone never creates or clears a banner.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clean_passes_alone_show_no_banner()
    {
        (ShellHarness harness, Account accountA, Account accountB) = await InitializedAsync();

        harness.Controller.RaiseClassification(Summary(accountA.Id));
        harness.Controller.RaiseClassification(Summary(accountB.Id));

        Assert.Null(harness.Shell.Banner);
    }

    // Case 14: counts are added over the last summaries of the accounts.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Counts_of_several_accounts_are_added()
    {
        (ShellHarness harness, Account accountA, Account accountB) = await InitializedAsync();

        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7));
        harness.Controller.RaiseClassification(Summary(accountB.Id, evaluationFailed: 5));

        Assert.Contains("12", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 14 (fix.2): every pass adds to the sums of its account since app start.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Passes_of_the_same_account_add_up_since_app_start()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();
        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7));

        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 3));

        Assert.Contains("10", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 14: unknown account.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Summary_of_an_unknown_account_is_ignored()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();

        harness.Controller.RaiseClassification(Summary(Guid.NewGuid(), evaluationFailed: 9, brakeHeld: 9));

        Assert.Null(harness.Shell.Banner);

        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7));
        string? before = harness.Shell.Banner;
        harness.Controller.RaiseClassification(Summary(Guid.NewGuid(), brakeHeld: 9));

        Assert.Equal(before, harness.Shell.Banner);
        Assert.Empty(harness.Log.Entries);
    }

    // D4: ReadFailed alone shows no banner.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_failed_alone_shows_no_banner_and_does_not_enter_the_text()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();

        harness.Controller.RaiseClassification(Summary(accountA.Id, readFailed: 6));

        Assert.Null(harness.Shell.Banner);

        harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7, readFailed: 6));

        Assert.DoesNotContain("6", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Suspended_act_shows_a_fixed_waiting_text_without_a_count_and_clears_afterwards()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();

        harness.Controller.RaiseClassification(Summary(accountA.Id, actSuspended: true));

        string? waiting = harness.Shell.Banner;
        Assert.False(string.IsNullOrWhiteSpace(waiting));
        Assert.DoesNotContain(waiting!, character => char.IsDigit(character));

        harness.Controller.RaiseClassification(Summary(accountA.Id, actSuspended: true));
        Assert.Equal(waiting, harness.Shell.Banner);

        harness.Controller.RaiseClassification(Summary(accountA.Id));
        Assert.Null(harness.Shell.Banner);
    }

    // Case 15: the waiting text differs from the problem text, and both can be shown together.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Waiting_text_and_problem_text_are_different_texts()
    {
        (ShellHarness harness, Account accountA, Account accountB) = await InitializedAsync();
        harness.Controller.RaiseClassification(Summary(accountA.Id, actSuspended: true));
        string waiting = harness.Shell.Banner!;
        harness.Controller.RaiseClassification(Summary(accountA.Id));
        harness.Controller.RaiseClassification(Summary(accountB.Id, evaluationFailed: 7));
        string problem = harness.Shell.Banner!;

        harness.Controller.RaiseClassification(Summary(accountA.Id, actSuspended: true));

        Assert.NotEqual(waiting, problem);
        Assert.Contains("7", harness.Shell.Banner, StringComparison.Ordinal);
        Assert.NotEqual(problem, harness.Shell.Banner);
    }

    // Case 16: the event arrives on a thread-pool thread and is marshalled through the dispatcher.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Summary_event_is_marshalled_through_the_dispatcher()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();
        harness.Dispatcher.Queueing = true;
        int postsBefore = harness.Dispatcher.PostCount;

        await harness.Controller.RaiseClassificationOnPoolAsync(Summary(accountA.Id, evaluationFailed: 7));

        Assert.True(harness.Dispatcher.PostCount > postsBefore);
        Assert.Null(harness.Shell.Banner);

        harness.Dispatcher.Pump();

        Assert.Contains("7", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 16: an exception in the handler is logged by type name and never propagates.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exception_in_the_summary_handler_is_logged_by_type_name_and_does_not_propagate()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();
        harness.Dispatcher.FailureOnPost = new InvalidOperationException(MarkerSecret);

        Exception? failure = Record.Exception(() => harness.Controller.RaiseClassification(Summary(accountA.Id, evaluationFailed: 7)));

        Assert.Null(failure);
        Assert.Contains("InvalidOperationException", harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(MarkerSecret, harness.Shell.Banner ?? string.Empty, StringComparison.Ordinal);
    }

    // Case 16: the same on a pool thread (the real situation).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exception_in_the_summary_handler_does_not_propagate_on_a_pool_thread_either()
    {
        (ShellHarness harness, Account accountA, _) = await InitializedAsync();
        harness.Dispatcher.FailureOnPost = new InvalidOperationException(MarkerSecret);

        Exception? failure = await Record.ExceptionAsync(
            () => harness.Controller.RaiseClassificationOnPoolAsync(Summary(accountA.Id, brakeHeld: 2)));

        Assert.Null(failure);
        Assert.Contains("InvalidOperationException", harness.Log.AllText, StringComparison.Ordinal);
    }
}

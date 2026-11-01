using MailWarden.Infrastructure.Background;
using static MailWarden.Presentation.Tests.TestSupport.ActionsTestHelpers;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b D3 (case 17): <see cref="SearchViewModel"/> against the fake read model.</summary>
[Trait("Category", "feature:ui")]
public sealed class SearchViewModelTests
{
    private const string SecretQuery = "marker_query_secret";

    private static async Task<(ShellHarness Harness, Account Account, MailFolder Inbox)> SeededAsync()
    {
        var harness = new ShellHarness();
        Account account = harness.ReadModel.AddAccount("Account_A");
        MailFolder inbox = harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX");
        MailFolder trash = harness.ReadModel.AddFolder(account, FolderRole.Trash, "Trash");
        harness.ReadModel.AddMessage(inbox, "invoice_alpha_one", fromName: "marker_sender_x");
        harness.ReadModel.AddMessage(inbox, "invoice_alpha_two", fromName: "marker_sender_x");
        harness.ReadModel.AddMessage(inbox, "newsletter_beta", fromName: "marker_sender_x");
        harness.ReadModel.AddMessage(trash, "invoice_alpha_trashed", fromName: "marker_sender_x");
        harness.SetStatuses(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(3);
        return (harness, account, inbox);
    }

    private static string[] Subjects(ShellHarness harness) =>
        [.. harness.Shell.Messages.Items.Select(row => row.Subject).Order()];

    // Case 17: a search shows its result like a view, with the account label, using the limit 500.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Search_shows_the_matching_mails_with_account_label_and_limit_500()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.Shell.Search.Query = "alpha";

        await harness.Shell.Search.SearchAsync();

        Assert.Equal(new[] { "invoice_alpha_one", "invoice_alpha_trashed", "invoice_alpha_two" }, Subjects(harness));
        Assert.All(harness.Shell.Messages.Items, row => Assert.Equal("Account_A", row.AccountLabel));
        (string Text, int Limit) call = Assert.Single(harness.ReadModel.SearchCalls);
        Assert.Equal("alpha", call.Text);
        Assert.Equal(500, call.Limit);
        Assert.False(harness.Shell.Search.IsSearching);
        Assert.Empty(harness.Log.Entries);
    }

    // Case 17: no hit gives an empty list, not an error.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_search_without_hits_shows_an_empty_list()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.Shell.Search.Query = "zzz_no_such_text";

        await harness.Shell.Search.SearchAsync();

        Assert.Empty(harness.Shell.Messages.Items);
        Assert.Null(harness.Shell.Banner);
    }

    // Case 17: empty or blank text goes back to the previous view without asking the read model.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public async Task Empty_or_blank_text_returns_to_the_previous_view_without_a_search(string blank)
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.Shell.Search.Query = "alpha";
        await harness.Shell.Search.SearchAsync();
        Assert.Equal(3, harness.Shell.Messages.Items.Count);
        int searchesBefore = harness.ReadModel.SearchCalls.Count;
        harness.Shell.Search.Query = blank;

        await harness.Shell.Search.SearchAsync();

        Assert.Equal(searchesBefore, harness.ReadModel.SearchCalls.Count);
        Assert.Equal(new[] { "invoice_alpha_one", "invoice_alpha_two", "newsletter_beta" }, Subjects(harness));
    }

    // Case 17: a blank query without any earlier search also does not throw or search.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Blank_query_without_an_earlier_search_does_nothing()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.Shell.Search.Query = " ";

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Search.SearchAsync());

        Assert.Null(failure);
        Assert.Empty(harness.ReadModel.SearchCalls);
        Assert.Equal(3, harness.Shell.Messages.Items.Count);
    }

    // Case 17: the stale guard - the newest search wins even when an older one answers last.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_newest_search_wins_when_an_older_one_answers_late()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.SearchGate = (_, callNumber) => callNumber == 1 ? releaseFirst.Task : Task.CompletedTask;
        harness.Shell.Search.Query = "alpha";
        Task first = harness.Shell.Search.SearchAsync();
        await Wait.UntilAsync(() => harness.ReadModel.SearchCalls.Count == 1, "first search started and is blocked");
        Assert.True(harness.Shell.Search.IsSearching);
        harness.Shell.Search.Query = "beta";

        await harness.Shell.Search.SearchAsync();
        releaseFirst.SetResult();
        await first;
        await Wait.NegativeWindowAsync();

        Assert.Equal(new[] { "newsletter_beta" }, Subjects(harness));
        Assert.False(harness.Shell.Search.IsSearching);
    }

    // Case 17: a blank query after a slow search also drops the late result.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_blank_query_discards_the_result_of_a_search_that_is_still_running()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.SearchGate = (_, _) => release.Task;
        harness.Shell.Search.Query = "alpha";
        Task slow = harness.Shell.Search.SearchAsync();
        await Wait.UntilAsync(() => harness.ReadModel.SearchCalls.Count == 1, "search started and is blocked");
        harness.Shell.Search.Query = string.Empty;

        await harness.Shell.Search.SearchAsync();
        release.SetResult();
        await slow;
        await Wait.NegativeWindowAsync();

        Assert.Equal(new[] { "invoice_alpha_one", "invoice_alpha_two", "newsletter_beta" }, Subjects(harness));
    }

    // Case 17: syntax characters never throw, whether the store accepts them or not.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("\"")]
    [InlineData("a\"b")]
    [InlineData("*")]
    [InlineData("alpha AND")]
    [InlineData("NOT")]
    [InlineData("(alpha OR")]
    [InlineData("NEAR(alpha beta, 3")]
    [InlineData("col:alpha")]
    [InlineData("alpha^")]
    [InlineData("‮alpha\u0007")]
    public async Task Syntax_characters_do_not_throw_even_when_the_read_model_rejects_them(string text)
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.ReadModel.SearchFailure = new ArgumentException("fts5: syntax error");
        harness.Shell.Search.Query = text;

        Exception? failure = await Record.ExceptionAsync(() => harness.Shell.Search.SearchAsync());

        Assert.Null(failure);
        Assert.False(harness.Shell.Search.IsSearching);
        Assert.Empty(harness.Shell.Messages.Items);
        AssertNoPrivateText(harness.Shell.Banner);
    }

    // Case 17: the same characters reach an accepting read model unchanged (the view model does not mangle the text).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_query_text_is_passed_to_the_read_model_unchanged_apart_from_trimming()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.Shell.Search.Query = "  a\"b *  ";

        await harness.Shell.Search.SearchAsync();

        string sent = Assert.Single(harness.ReadModel.SearchCalls).Text;
        Assert.Equal("a\"b *", sent.Trim());
    }

    // Case 17: a pending delete hides the row in the result too.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_row_hidden_by_a_pending_delete_is_not_in_the_search_result()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        MessageRowViewModel victim = harness.Shell.Messages.Items.Single(row => row.Subject == "invoice_alpha_one");
        await SelectAsync(harness, victim);
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 2, "row hidden");
        harness.Shell.Search.Query = "alpha";

        await harness.Shell.Search.SearchAsync();

        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Message.Id == victim.Message.Id);
        Assert.Equal(new[] { "invoice_alpha_trashed", "invoice_alpha_two" }, Subjects(harness));
    }

    // Case 17: an invalid text and a failing store leave nothing of the query in the log.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_search_text_goes_to_no_log()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.ReadModel.SearchFailure = new InvalidOperationException(SecretQuery);
        harness.Shell.Search.Query = SecretQuery;

        await harness.Shell.Search.SearchAsync();
        harness.ReadModel.SearchFailure = null;
        await harness.Shell.Search.SearchAsync();

        Assert.DoesNotContain(SecretQuery, harness.Log.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretQuery, harness.Shell.Banner ?? string.Empty, StringComparison.Ordinal);
    }

    // Case 17: selecting a mail from the result shows it in the reading pane like in any view.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_result_row_can_be_selected_and_read()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        harness.Shell.Search.Query = "beta";
        await harness.Shell.Search.SearchAsync();

        await SelectAsync(harness, Assert.Single(harness.Shell.Messages.Items));

        Assert.Equal("newsletter_beta", harness.Shell.Reading.Subject);
    }

    // Case 17 / 20: a search that answers after Dispose changes nothing and logs nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_search_answering_after_dispose_changes_nothing()
    {
        (ShellHarness harness, _, _) = await SeededAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ReadModel.SearchGate = (_, _) => release.Task;
        harness.Shell.Search.Query = "alpha";
        Task slow = harness.Shell.Search.SearchAsync();
        await Wait.UntilAsync(() => harness.ReadModel.SearchCalls.Count == 1, "search started and is blocked");

        harness.Shell.Dispose();
        release.SetResult();
        Exception? failure = await Record.ExceptionAsync(() => slow);
        await Wait.NegativeWindowAsync();

        Assert.Null(failure);
        Assert.DoesNotContain(harness.Shell.Messages.Items, row => row.Subject == "invoice_alpha_trashed");
        Assert.Empty(harness.Log.Entries);
    }
}

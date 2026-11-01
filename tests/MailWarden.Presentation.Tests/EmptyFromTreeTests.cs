using System.ComponentModel;
using MailWarden.Infrastructure.Background;
using static MailWarden.Presentation.Tests.TestSupport.ActionsTestHelpers;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04b after the manual test (X1, X2, X3): "Ordner leeren" from the tree and the unread numbers of the tree.</summary>
[Trait("Category", "feature:ui")]
public sealed class EmptyFromTreeTests
{
    private sealed record Setup(
        ShellHarness Harness,
        Account AccountA,
        Account AccountB,
        MailFolder InboxA,
        MailFolder JunkA,
        MailFolder TrashA,
        MailFolder InboxB,
        MailFolder JunkB,
        MailFolder TrashB);

    private static IEnumerable<NavigationItem> Flatten(IEnumerable<NavigationItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(Flatten(item.Children)));

    private static IEnumerable<NavigationItem> NodesOf(ShellHarness harness, params MailView[] views) =>
        Flatten(harness.Shell.Navigation.Items).Where(item => item.View is not null && views.Contains(item.View));

    /// <summary>Two accounts, each with Inbox, Sent, Archive, a custom folder, Trash and Junk. Junk of B holds 3 unread mails.</summary>
    private static async Task<Setup> SeededAsync(bool secondAccountHasJunk = true, int junkBCount = 3)
    {
        var harness = new ShellHarness();
        FakeMailboxReadModel model = harness.ReadModel;
        Account accountA = model.AddAccount("Account_A");
        Account accountB = model.AddAccount("Account_B");
        MailFolder inboxA = model.AddFolder(accountA, FolderRole.Inbox, "INBOX");
        model.AddFolder(accountA, FolderRole.Sent, "Sent");
        model.AddFolder(accountA, FolderRole.Archive, "Archive");
        model.AddFolder(accountA, FolderRole.None, "Custom/Sub");
        MailFolder trashA = model.AddFolder(accountA, FolderRole.Trash, "Trash");
        MailFolder junkA = model.AddFolder(accountA, FolderRole.Junk, "Junk");
        MailFolder inboxB = model.AddFolder(accountB, FolderRole.Inbox, "INBOX");
        MailFolder trashB = model.AddFolder(accountB, FolderRole.Trash, "Trash");
        MailFolder junkB = secondAccountHasJunk ? model.AddFolder(accountB, FolderRole.Junk, "Junk") : inboxB;
        model.AddMessage(inboxA, "marker_subject_a1", fromName: "marker_sender_x");
        model.AddMessage(inboxA, "marker_subject_a2", fromName: "marker_sender_x");
        model.AddMessage(inboxB, "marker_subject_b1", fromName: "marker_sender_x");
        model.AddMessage(junkA, "marker_subject_junk_a1", fromName: "marker_sender_x");
        model.AddMessage(trashA, "marker_subject_trash_a1", fromName: "marker_sender_x");
        model.AddMessage(trashB, "marker_subject_trash_b1", fromName: "marker_sender_x");
        if (secondAccountHasJunk)
        {
            for (int index = 0; index < junkBCount; index++)
            {
                model.AddMessage(junkB, $"marker_subject_junk_b{index}", fromName: "marker_sender_x");
            }
        }

        harness.SetStatuses(
            FakeMailboxController.Status(accountA.Id, AccountSyncState.Idle),
            FakeMailboxController.Status(accountB.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(3);
        return new Setup(harness, accountA, accountB, inboxA, junkA, trashA, inboxB, junkB, trashB);
    }

    // ---- X-a .. X-d: which entries can be emptied -----------------------------------------------------------

    // X-a: Trash and Junk of one account.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task X_a_trash_and_junk_of_one_account_can_be_emptied()
    {
        Setup setup = await SeededAsync();

        NavigationItem[] trash = [.. NodesOf(setup.Harness, MailView.Folder(setup.AccountA.Id, setup.TrashA.Id), MailView.GroupForAccount(FolderRole.Trash, setup.AccountA.Id))];
        NavigationItem[] junk = [.. NodesOf(setup.Harness, MailView.Folder(setup.AccountA.Id, setup.JunkA.Id), MailView.GroupForAccount(FolderRole.Junk, setup.AccountA.Id))];

        Assert.NotEmpty(trash);
        Assert.All(trash, item => Assert.True(item.CanEmptyFolder));
        Assert.All(junk, item => Assert.True(item.CanEmptyFolder));
        Assert.True(setup.Harness.Shell.EmptyFolderOfViewCommand.CanExecute(MailView.Folder(setup.AccountA.Id, setup.TrashA.Id)));
        Assert.True(setup.Harness.Shell.EmptyFolderOfViewCommand.CanExecute(MailView.Folder(setup.AccountA.Id, setup.JunkA.Id)));
    }

    // X-b: everything else cannot be emptied.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_b_other_folders_account_nodes_and_parents_cannot_be_emptied()
    {
        Setup setup = await SeededAsync();
        FakeMailboxReadModel model = setup.Harness.ReadModel;
        MailFolder[] otherFolders = [.. (await model.ListFoldersAsync(setup.AccountA.Id)).Where(folder => folder.Role is not (FolderRole.Trash or FolderRole.Junk))];
        Assert.True(otherFolders.Length >= 4, "Inbox, Sent, Archive and the custom folder are seeded");

        NavigationItem[] all = [.. Flatten(setup.Harness.Shell.Navigation.Items)];
        foreach (NavigationItem item in all)
        {
            bool isTrashOrJunkEntry = item.View is { } view && (
                view == MailView.Folder(setup.AccountA.Id, setup.TrashA.Id) || view == MailView.Folder(setup.AccountA.Id, setup.JunkA.Id) ||
                view == MailView.Folder(setup.AccountB.Id, setup.TrashB.Id) || view == MailView.Folder(setup.AccountB.Id, setup.JunkB.Id) ||
                view == MailView.GroupForAccount(FolderRole.Trash, setup.AccountA.Id) || view == MailView.GroupForAccount(FolderRole.Junk, setup.AccountA.Id) ||
                view == MailView.GroupForAccount(FolderRole.Trash, setup.AccountB.Id) || view == MailView.GroupForAccount(FolderRole.Junk, setup.AccountB.Id) ||
                view == MailView.Group(FolderRole.Junk) || view == MailView.Group(FolderRole.Trash));
            if (!isTrashOrJunkEntry)
            {
                Assert.False(item.CanEmptyFolder, $"entry '{item.Label}' must not offer emptying");
            }
        }

        // The command only needs a MailView; the emptiable check runs at execution. Executing it must do nothing,
        // also when the folder holds a real mail (so it cannot pass just because the folder is empty).
        foreach (MailFolder folder in otherFolders)
        {
            if ((await setup.Harness.ReadModel.ListMessagesAsync(new MessageQuery(FolderId: folder.Id, Limit: 10))).Count == 0)
            {
                setup.Harness.ReadModel.AddMessage(folder, $"marker_subject_in_{folder.FullName}", fromName: "marker_sender_x");
            }

            setup.Harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Folder(folder.AccountId, folder.Id));
        }

        await Wait.NegativeWindowAsync();
        Assert.Empty(setup.Harness.ConfirmationTexts);
        Assert.Empty(setup.Harness.Actions.CallsNamed("EmptyFolder"));
        Assert.Empty(setup.Harness.Actions.Calls);
        NavigationItem accountNode = setup.Harness.AccountNode(setup.AccountA.Id);
        Assert.False(accountNode.CanEmptyFolder);
        Assert.NotEmpty(accountNode.Children);
    }

    // X-c: Junk group with one account that has the folder; Trash on its group view.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_c_group_junk_with_one_account_can_be_emptied_and_so_can_trash_on_its_group_view()
    {
        var harness = new ShellHarness();
        Account account = harness.ReadModel.AddAccount("Account_A");
        harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(account, FolderRole.Inbox, "INBOX"), "marker_subject_i");
        harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(account, FolderRole.Junk, "Junk"), "marker_subject_j");
        harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(account, FolderRole.Trash, "Trash"), "marker_subject_t");
        harness.SetStatuses(FakeMailboxController.Status(account.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(1);

        NavigationItem junkGroup = Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk)));

        Assert.True(junkGroup.CanEmptyFolder);
        Assert.True(harness.Shell.EmptyFolderOfViewCommand.CanExecute(MailView.Group(FolderRole.Junk)));
        Assert.True(harness.Shell.EmptyFolderOfViewCommand.CanExecute(MailView.Group(FolderRole.Trash)));
    }

    // X-d: two accounts having the folder.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_d_group_junk_and_trash_with_two_accounts_cannot_be_emptied()
    {
        Setup setup = await SeededAsync();

        NavigationItem junkGroup = Assert.Single(NodesOf(setup.Harness, MailView.Group(FolderRole.Junk)));

        Assert.False(junkGroup.CanEmptyFolder);

        // CanExecute only says the parameter is a MailView; executing on a view that cannot be emptied must do nothing.
        setup.Harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Group(FolderRole.Junk));
        setup.Harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Group(FolderRole.Trash));
        await Wait.NegativeWindowAsync();

        Assert.Empty(setup.Harness.ConfirmationTexts);
        Assert.Empty(setup.Harness.Actions.Calls);
    }

    // X-d / refresh: the flag follows the accounts on every refresh and raises PropertyChanged.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task CanEmptyFolder_is_updated_on_refresh_and_raises_PropertyChanged()
    {
        var harness = new ShellHarness();
        Account accountA = harness.ReadModel.AddAccount("Account_A");
        harness.ReadModel.AddMessage(harness.ReadModel.AddFolder(accountA, FolderRole.Inbox, "INBOX"), "marker_subject_i");
        harness.ReadModel.AddFolder(accountA, FolderRole.Junk, "Junk");
        harness.SetStatuses(FakeMailboxController.Status(accountA.Id, AccountSyncState.Idle));
        await harness.InitializeAndWaitForListAsync(1);
        NavigationItem before = Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk)));
        Assert.True(before.CanEmptyFolder);
        List<string?> changed = [];
        ((INotifyPropertyChanged)before).PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Account accountB = harness.ReadModel.AddAccount("Account_B");
        harness.ReadModel.AddFolder(accountB, FolderRole.Inbox, "INBOX");
        harness.ReadModel.AddFolder(accountB, FolderRole.Junk, "Junk");
        harness.SetStatuses(
            FakeMailboxController.Status(accountA.Id, AccountSyncState.Idle),
            FakeMailboxController.Status(accountB.Id, AccountSyncState.Idle));

        await SyncCycleAsync(harness, accountA);

        NavigationItem after = Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk)));
        Assert.False(after.CanEmptyFolder);
        Assert.True(ReferenceEquals(before, after) ? changed.Contains(nameof(NavigationItem.CanEmptyFolder)) : true);
    }

    // ---- X-e .. X-g: empty from the tree ------------------------------------------------------------------

    // X-e: the question names the right-clicked folder's account and count; list and selection stay; no row-removed event.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_e_empty_of_another_accounts_junk_asks_for_that_folder_and_leaves_list_and_selection_alone()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        MessageRowViewModel selected = harness.Shell.Messages.Items[0];
        await SelectAsync(harness, selected);
        long[] listBefore = [.. harness.Shell.Messages.Items.Select(row => row.Message.Id)];
        int removedEvents = 0;
        harness.Shell.ActionRowsRemoved += (_, _) => Interlocked.Increment(ref removedEvents);
        long[] junkBIds = [.. (await harness.ReadModel.ListMessagesAsync(new MessageQuery(FolderId: setup.JunkB.Id, Limit: 100))).Select(message => message.Id)];

        harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Folder(setup.AccountB.Id, setup.JunkB.Id));

        await WaitForConfirmationAsync(harness);
        await WaitForCallsAsync(harness, "EmptyFolder");
        await Wait.NegativeWindowAsync();
        string text = Assert.Single(harness.ConfirmationTexts);
        Assert.Contains("Account_B", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Account_A", text, StringComparison.Ordinal);
        Assert.Contains("3", text, StringComparison.Ordinal);
        AssertNoPrivateText(text);
        ActionCall call = Assert.Single(harness.Actions.CallsNamed("EmptyFolder"));
        Assert.Equal(setup.JunkB.Id, call.FolderId);
        Assert.Equal(junkBIds.Order().ToArray(), call.MessageIds.Order().ToArray());
        Assert.Equal(listBefore, harness.Shell.Messages.Items.Select(row => row.Message.Id).ToArray());
        Assert.Equal(new[] { selected.Message.Id }, harness.Shell.Messages.SelectedRows.Select(row => row.Message.Id).ToArray());
        Assert.Equal(0, Volatile.Read(ref removedEvents));
    }

    // X-e control: rows removed from the current list do raise ActionRowsRemoved.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ActionRowsRemoved_fires_when_rows_leave_the_current_list()
    {
        Setup setup = await SeededAsync();
        int removedEvents = 0;
        setup.Harness.Shell.ActionRowsRemoved += (_, _) => Interlocked.Increment(ref removedEvents);
        await SelectAsync(setup.Harness, setup.Harness.Shell.Messages.Items[0]);

        setup.Harness.Shell.DeleteCommand.Execute(null);

        await Wait.UntilAsync(() => Volatile.Read(ref removedEvents) >= 1, "event for the removed row");
    }

    // X-f: an active search does not block it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_f_empty_from_the_tree_works_while_a_search_is_active()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        harness.Shell.Search.Query = "marker_subject_a";
        await harness.Shell.Search.SearchAsync();
        MailView junkB = MailView.Folder(setup.AccountB.Id, setup.JunkB.Id);

        Assert.True(harness.Shell.EmptyFolderOfViewCommand.CanExecute(junkB));
        harness.Shell.EmptyFolderOfViewCommand.Execute(junkB);

        await WaitForConfirmationAsync(harness);
        await WaitForCallsAsync(harness, "EmptyFolder");
        Assert.Equal(setup.JunkB.Id, Assert.Single(harness.Actions.CallsNamed("EmptyFolder")).FolderId);
    }

    // X-g: a folder without local mails asks nothing and stores nothing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_g_a_folder_without_local_mails_asks_nothing_and_stores_nothing()
    {
        Setup setup = await SeededAsync(junkBCount: 0);
        ShellHarness harness = setup.Harness;

        harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Folder(setup.AccountB.Id, setup.JunkB.Id));
        await Wait.NegativeWindowAsync();

        Assert.Empty(harness.ConfirmationTexts);
        Assert.Empty(harness.Actions.Calls);
    }

    // X2 control: the answer "no" stores nothing either.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Answering_no_in_the_tree_dialog_stores_nothing()
    {
        Setup setup = await SeededAsync();
        setup.Harness.ConfirmationAnswer = false;

        setup.Harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Folder(setup.AccountB.Id, setup.JunkB.Id));

        await WaitForConfirmationAsync(setup.Harness);
        await Wait.NegativeWindowAsync();
        Assert.Empty(setup.Harness.Actions.Calls);
    }

    // ---- X-i .. X-m: unread numbers ---------------------------------------------------------------------

    private static int[] UnreadOf(ShellHarness harness, params MailView[] views) =>
        [.. NodesOf(harness, views).Select(item => item.UnreadCount)];

    private static MailView[] InboxViews(Setup setup) =>
        [MailView.Group(FolderRole.Inbox), MailView.Folder(setup.AccountA.Id, setup.InboxA.Id), MailView.GroupForAccount(FolderRole.Inbox, setup.AccountA.Id)];

    private static (TaskCompletionSource Release, Func<int> Entered) BlockCalls(ShellHarness harness)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        harness.Actions.Gate = async () =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
        };
        return (release, () => Volatile.Read(ref entered));
    }

    private static MessageRowViewModel RowOfInboxA(Setup setup) =>
        setup.Harness.Shell.Messages.Items.First(row => row.Message.FolderId == setup.InboxA.Id);

    // X-i: unread mail, Del: the numbers of folder and group drop before the command runs.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_i_delete_of_an_unread_mail_lowers_the_numbers_before_the_command_runs()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        (TaskCompletionSource release, Func<int> entered) = BlockCalls(harness);
        Assert.Equal(3, Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount);
        await SelectAsync(harness, RowOfInboxA(setup));

        harness.Shell.DeleteCommand.Execute(null);
        await Wait.UntilAsync(() => entered() == 1, "delete reached the gate (not stored yet)");

        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount == 2, "group number dropped");
        Assert.All(NodesOf(harness, MailView.Folder(setup.AccountA.Id, setup.InboxA.Id), MailView.GroupForAccount(FolderRole.Inbox, setup.AccountA.Id)), item => Assert.Equal(1, item.UnreadCount));
        Assert.Empty(harness.Actions.Calls);
        release.SetResult();
    }

    // X-j: a refused action brings the number back.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_j_a_refused_delete_brings_the_number_back()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        harness.Actions.Failure = new MailWarden.Infrastructure.Composition.MailActionException("fixed rejection text");
        await SelectAsync(harness, RowOfInboxA(setup));

        harness.Shell.DeleteCommand.Execute(null);

        await Wait.UntilAsync(() => !string.IsNullOrWhiteSpace(harness.Shell.Banner), "refusal shown");
        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount == 3, "number back at 3");
    }

    // X-j: a command that failed later brings the number back after the next refresh.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_j_a_command_that_failed_brings_the_number_back_after_the_refresh()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        await SelectAsync(harness, RowOfInboxA(setup));
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount == 2, "number dropped");

        harness.Actions.Fail(Assert.Single(harness.Actions.CommandIds));
        await SyncCycleAsync(harness, setup.AccountA);

        Assert.Equal(3, Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount);
    }

    // X-k: a read mail leaves the number alone.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_k_delete_of_a_read_mail_leaves_the_number_unchanged()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        MailMessage seen = harness.ReadModel.AddMessage(setup.InboxA, "marker_subject_seen", flags: MessageFlags.Seen, fromName: "marker_sender_x");
        await SyncCycleAsync(harness, setup.AccountA);
        Assert.Equal(3, Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount);
        await SelectAsync(harness, harness.Shell.Messages.Items.Single(row => row.Message.Id == seen.Id));

        harness.Shell.DeleteCommand.Execute(null);

        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.NegativeWindowAsync();
        Assert.Equal(3, Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount);
    }

    // X3: permanent delete and move lower the number as well.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_and_move_lower_the_number_too()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        MailFolder archive = harness.ReadModel.AddFolder(setup.AccountA, FolderRole.Archive, "Archive2");
        MessageRowViewModel[] rowsA = [.. harness.Shell.Messages.Items.Where(row => row.Message.FolderId == setup.InboxA.Id)];
        await SelectAsync(harness, rowsA[0]);
        harness.Shell.DeletePermanentlyCommand.Execute(null);
        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount == 2, "permanent delete lowered the number");

        await SelectAsync(harness, harness.Shell.Messages.Items.First(row => row.Message.FolderId == setup.InboxA.Id));
        harness.Shell.MoveCommand.Execute(archive.Id);

        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount == 1, "move lowered the number");
    }

    // X-l: an empty folder whose mails are not in the current list: the number is 0 once the command is stored.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_l_empty_of_a_folder_outside_the_current_list_sets_its_number_to_zero_once_stored()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        MailView junkBView = MailView.Folder(setup.AccountB.Id, setup.JunkB.Id);
        NavigationItem[] junkNodes = [.. NodesOf(harness, MailView.GroupForAccount(FolderRole.Junk, setup.AccountB.Id), junkBView)];
        NavigationItem junkGroup = Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk)));
        Assert.Equal(4, junkGroup.UnreadCount); // 1 of A + 3 of B

        harness.Shell.EmptyFolderOfViewCommand.Execute(junkBView);

        await WaitForCallsAsync(harness, "EmptyFolder");
        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk))).UnreadCount == 1, "group number lost the 3 of account B");
        Assert.All(junkNodes, item => Assert.Equal(0, item.UnreadCount));
    }

    // X-l: many unread mails, more than one loaded page, in the viewed folder.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds * 2)]
    public async Task X_l_empty_of_a_viewed_folder_with_more_unread_mails_than_one_page_gives_zero()
    {
        Setup setup = await SeededAsync(junkBCount: 250);
        ShellHarness harness = setup.Harness;
        MailView junkBView = MailView.Folder(setup.AccountB.Id, setup.JunkB.Id);
        NavigationItem node = Flatten(harness.Shell.Navigation.Items).First(item => item.View == junkBView || item.View == MailView.GroupForAccount(FolderRole.Junk, setup.AccountB.Id));
        harness.Shell.Navigation.SelectedItem = node;
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count > 0, "junk of B listed");

        harness.Shell.EmptyFolderOfViewCommand.Execute(junkBView);

        await WaitForCallsAsync(harness, "EmptyFolder");
        await Wait.UntilAsync(() => node.UnreadCount == 0, "number 0 although only one page was loaded");
        Assert.Equal(250, Assert.Single(harness.Actions.CallsNamed("EmptyFolder")).MessageIds.Count);
    }

    // X-m: the command has run - the number stays correct and is not subtracted twice.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_m_after_the_command_ran_the_number_is_not_subtracted_twice()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        await SelectAsync(harness, RowOfInboxA(setup));
        harness.Shell.DeleteCommand.Execute(null);
        await WaitForCallsAsync(harness, "MoveToTrash");
        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount == 2, "number dropped");

        harness.Actions.Complete(Assert.Single(harness.Actions.CommandIds));
        await SyncCycleAsync(harness, setup.AccountA);
        await SyncCycleAsync(harness, setup.AccountA);

        Assert.Equal(2, Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Inbox))).UnreadCount);
    }

    // X-m: same for the empty folder.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task X_m_after_an_empty_ran_the_number_stays_correct()
    {
        Setup setup = await SeededAsync();
        ShellHarness harness = setup.Harness;
        harness.Shell.EmptyFolderOfViewCommand.Execute(MailView.Folder(setup.AccountB.Id, setup.JunkB.Id));
        await WaitForCallsAsync(harness, "EmptyFolder");
        await Wait.UntilAsync(() => Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk))).UnreadCount == 1, "number dropped");

        harness.Actions.Complete(Assert.Single(harness.Actions.CommandIds));
        await SyncCycleAsync(harness, setup.AccountB);

        Assert.Equal(1, Assert.Single(NodesOf(harness, MailView.Group(FolderRole.Junk))).UnreadCount);
    }
}

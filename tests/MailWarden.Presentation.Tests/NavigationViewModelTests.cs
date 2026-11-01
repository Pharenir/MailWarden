using MailWarden.Infrastructure.Background;

namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a cases 29 and 30 (D10): navigation items, default selection and ignored selections.</summary>
[Trait("Category", "feature:ui")]
public sealed class NavigationViewModelTests
{
    private static readonly string[] GroupLabels = ["Posteingang", "Gesendet", "Postausgang", "Screener", "Junk", "Quarantäne"];

    private static IEnumerable<NavigationItem> Flatten(IEnumerable<NavigationItem> items) =>
        items.SelectMany(item => new[] { item }.Concat(Flatten(item.Children)));

    private sealed record TwoAccounts(
        Account AccountA,
        Account AccountB,
        MailFolder InboxA,
        MailFolder DraftsA,
        MailFolder ArchiveChildA,
        MailFolder InboxB,
        MailFolder TrashB);

    private static TwoAccounts SeedTwoAccounts(ShellHarness harness)
    {
        FakeMailboxReadModel model = harness.ReadModel;
        Account accountA = model.AddAccount("Account_A");
        Account accountB = model.AddAccount("Account_B");
        MailFolder inboxA = model.AddFolder(accountA, FolderRole.Inbox, "INBOX");
        MailFolder sentA = model.AddFolder(accountA, FolderRole.Sent, "Sent");
        MailFolder junkA = model.AddFolder(accountA, FolderRole.Junk, "Junk");
        MailFolder draftsA = model.AddFolder(accountA, FolderRole.Drafts, "Drafts");
        MailFolder archiveChildA = model.AddFolder(accountA, FolderRole.None, "Archive/2025");
        MailFolder inboxB = model.AddFolder(accountB, FolderRole.Inbox, "INBOX");
        MailFolder trashB = model.AddFolder(accountB, FolderRole.Trash, "Trash");
        model.AddMessage(inboxA);
        model.AddMessage(inboxA);
        model.AddMessage(inboxA, flags: MessageFlags.Seen);
        model.AddMessage(sentA);
        model.AddMessage(junkA);
        model.AddMessage(draftsA);
        model.AddMessage(archiveChildA, flags: MessageFlags.Seen);
        model.AddMessage(inboxB);
        model.AddMessage(trashB);
        return new TwoAccounts(accountA, accountB, inboxA, draftsA, archiveChildA, inboxB, trashB);
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task Six_group_items_come_first_with_labels_views_and_unread_counts_over_all_accounts()
    {
        var harness = new ShellHarness();
        SeedTwoAccounts(harness);

        await harness.Shell.InitializeAsync();

        NavigationItem[] groups = [.. harness.Shell.Navigation.Items.Take(6)];
        Assert.Equal(GroupLabels, groups.Select(item => item.Label).ToArray());
        Assert.Equal(MailView.GroupRoles.Select(MailView.Group).ToArray(), groups.Select(item => item.View!).ToArray());
        Assert.Equal(new[] { 3, 1, 0, 0, 1, 0 }, groups.Select(item => item.UnreadCount).ToArray());
        Assert.All(groups, item => Assert.Empty(item.Children));
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Each_account_gets_one_node_without_view_and_after_the_groups()
    {
        var harness = new ShellHarness();
        TwoAccounts seed = SeedTwoAccounts(harness);

        await harness.Shell.InitializeAsync();

        Assert.Equal(8, harness.Shell.Navigation.Items.Count);
        NavigationItem[] accountNodes = [.. harness.Shell.Navigation.Items.Skip(6)];
        Assert.Equal(new[] { "Account_A", "Account_B" }, accountNodes.Select(item => item.Label).OrderBy(label => label, StringComparer.Ordinal).ToArray());
        Assert.All(accountNodes, node => Assert.Null(node.View));
        Assert.All(accountNodes, node => Assert.Equal(0, node.UnreadCount));
        Assert.Contains(accountNodes, node => node.AccountId == seed.AccountA.Id);
        Assert.Contains(accountNodes, node => node.AccountId == seed.AccountB.Id);
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_children_are_the_six_role_views_then_the_tree_of_the_remaining_folders()
    {
        var harness = new ShellHarness();
        TwoAccounts seed = SeedTwoAccounts(harness);

        await harness.Shell.InitializeAsync();

        NavigationItem nodeA = harness.AccountNode(seed.AccountA.Id);
        Assert.Equal(
            MailView.GroupRoles.Select(role => MailView.GroupForAccount(role, seed.AccountA.Id)).ToArray(),
            nodeA.Children.Take(6).Select(item => item.View!).ToArray());
        Assert.Equal(new[] { 2, 1, 0, 0, 1, 0 }, nodeA.Children.Take(6).Select(item => item.UnreadCount).ToArray());

        NavigationItem[] remaining = [.. nodeA.Children.Skip(6)];
        Assert.Equal(new[] { "Archive", "Drafts" }, remaining.Select(item => item.Label).ToArray());
        NavigationItem archive = remaining[0];
        Assert.Null(archive.View);
        Assert.Equal(0, archive.UnreadCount);
        NavigationItem archiveChild = Assert.Single(archive.Children);
        Assert.Equal("2025", archiveChild.Label);
        Assert.Equal(MailView.Folder(seed.AccountA.Id, seed.ArchiveChildA.Id), archiveChild.View);
        Assert.Equal(0, archiveChild.UnreadCount);
        NavigationItem drafts = remaining[1];
        Assert.Equal(MailView.Folder(seed.AccountA.Id, seed.DraftsA.Id), drafts.View);
        Assert.Equal(1, drafts.UnreadCount);

        NavigationItem nodeB = harness.AccountNode(seed.AccountB.Id);
        Assert.Equal(
            MailView.GroupRoles.Select(role => MailView.GroupForAccount(role, seed.AccountB.Id)).ToArray(),
            nodeB.Children.Take(6).Select(item => item.View!).ToArray());
        NavigationItem trash = Assert.Single(nodeB.Children.Skip(6));
        Assert.Equal(MailView.Folder(seed.AccountB.Id, seed.TrashB.Id), trash.View);
        Assert.Equal(1, trash.UnreadCount);
    }

    // Case 29: folders with one of the six roles appear as role views only, never a second time as ordinary folders.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folders_with_a_group_role_do_not_appear_twice()
    {
        var harness = new ShellHarness();
        TwoAccounts seed = SeedTwoAccounts(harness);

        await harness.Shell.InitializeAsync();

        long[] folderViewIds = [.. Flatten(harness.Shell.Navigation.Items).Where(item => item.View?.FolderId is not null).Select(item => item.View!.FolderId!.Value)];
        Assert.Equal(
            new[] { seed.DraftsA.Id, seed.ArchiveChildA.Id, seed.TrashB.Id }.OrderBy(id => id).ToArray(),
            folderViewIds.OrderBy(id => id).ToArray());
        Assert.DoesNotContain(seed.InboxA.Id, folderViewIds);
        Assert.DoesNotContain(seed.InboxB.Id, folderViewIds);
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Default_selection_is_the_inbox_group_and_its_messages_are_listed()
    {
        var harness = new ShellHarness();
        SeedTwoAccounts(harness);

        await harness.Shell.InitializeAsync();

        Assert.Equal(MailView.Group(FolderRole.Inbox), harness.Shell.Navigation.SelectedItem?.View);
        await Wait.UntilAsync(() => harness.Shell.Messages.Items.Count == 4, "inbox messages of both accounts listed");
        Assert.Equal(FolderRole.Inbox, harness.ReadModel.ListQueries.Last().Role);
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_labels_are_cleaned_and_fall_back_to_the_address()
    {
        var harness = new ShellHarness();
        harness.ReadModel.AddAccount("Account\u202E X");
        harness.ReadModel.AddAccount(string.Empty, "fallback_user@example.test");

        await harness.Shell.InitializeAsync();

        string[] labels = [.. harness.Shell.Navigation.Items.Skip(6).Select(item => item.Label)];
        Assert.Contains("Account X", labels);
        Assert.Contains("fallback_user@example.test", labels);
        Assert.DoesNotContain(labels, label => label.Contains('\u202E', StringComparison.Ordinal));
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Folder_names_in_the_tree_are_cleaned()
    {
        var harness = new ShellHarness();
        Account account = harness.ReadModel.AddAccount("Account_A");
        harness.ReadModel.AddFolder(account, FolderRole.None, "Pro\u202Eject\u0007s");

        await harness.Shell.InitializeAsync();

        NavigationItem folder = Assert.Single(harness.AccountNode(account.Id).Children.Skip(6));
        Assert.Equal("Projects", folder.Label);
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Account_status_text_is_a_fixed_text_per_state_and_never_shows_error_details()
    {
        var harness = new ShellHarness();
        AccountSyncState[] states = [AccountSyncState.Idle, AccountSyncState.Running, AccountSyncState.WaitingAfterError, AccountSyncState.NeedsSignIn];
        Account[] accounts = [.. states.Select((state, index) => harness.ReadModel.AddAccount($"Account_{index}"))];
        harness.SetStatuses([.. states.Select((state, index) => FakeMailboxController.Status(
            accounts[index].Id,
            state,
            lastErrorType: state == AccountSyncState.WaitingAfterError ? "MailTransportException(Network)" : null))]);

        await harness.Shell.InitializeAsync();

        string[] texts = [.. accounts.Select(account => harness.AccountNode(account.Id).StatusText ?? string.Empty)];
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(texts, text => Assert.DoesNotContain("Exception", text, StringComparison.Ordinal));
    }

    // Case 29
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Without_accounts_only_the_group_items_exist_and_a_banner_points_to_the_auth_probe()
    {
        var harness = new ShellHarness();

        await harness.Shell.InitializeAsync();

        Assert.Equal(6, harness.Shell.Navigation.Items.Count);
        Assert.Equal(GroupLabels, harness.Shell.Navigation.Items.Select(item => item.Label).ToArray());
        Assert.All(harness.Shell.Navigation.Items, item => Assert.Equal(0, item.UnreadCount));
        Assert.NotNull(harness.Shell.Banner);
        Assert.Contains("AuthProbe", harness.Shell.Banner, StringComparison.Ordinal);
    }

    // Case 29 control: with accounts and healthy settings there is nothing to report.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task With_accounts_and_healthy_settings_the_banner_is_empty()
    {
        var harness = new ShellHarness();
        SeedTwoAccounts(harness);

        await harness.Shell.InitializeAsync();

        Assert.Null(harness.Shell.Banner);
    }

    // Case 30
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_an_account_node_without_view_leaves_the_selection_unchanged()
    {
        var harness = new ShellHarness();
        TwoAccounts seed = SeedTwoAccounts(harness);
        await harness.Shell.InitializeAsync();
        NavigationItem before = harness.Shell.Navigation.SelectedItem!;
        int callsBefore = harness.ReadModel.ListMessagesCalls;

        harness.Shell.Navigation.SelectedItem = harness.AccountNode(seed.AccountA.Id);

        Assert.Same(before, harness.Shell.Navigation.SelectedItem);
        await Wait.NegativeWindowAsync();
        Assert.Equal(callsBefore, harness.ReadModel.ListMessagesCalls);
    }

    // Case 30
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_a_tree_parent_without_view_leaves_the_selection_unchanged()
    {
        var harness = new ShellHarness();
        TwoAccounts seed = SeedTwoAccounts(harness);
        await harness.Shell.InitializeAsync();
        NavigationItem before = harness.Shell.Navigation.SelectedItem!;
        NavigationItem archiveParent = harness.AccountNode(seed.AccountA.Id).Children.Single(item => item.Label == "Archive");

        harness.Shell.Navigation.SelectedItem = archiveParent;

        Assert.Same(before, harness.Shell.Navigation.SelectedItem);
    }

    // Case 30 positive control: selecting an item with a view changes the selection and loads its messages.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Selecting_an_item_with_a_view_changes_the_selection_and_loads_the_list()
    {
        var harness = new ShellHarness();
        TwoAccounts seed = SeedTwoAccounts(harness);
        await harness.Shell.InitializeAsync();
        NavigationItem drafts = harness.AccountNode(seed.AccountA.Id).Children.Single(item => item.Label == "Drafts");

        harness.Shell.Navigation.SelectedItem = drafts;

        Assert.Same(drafts, harness.Shell.Navigation.SelectedItem);
        await Wait.UntilAsync(
            () => harness.Shell.Messages.Items.Count == 1 && harness.Shell.Messages.Items[0].Message.FolderId == seed.DraftsA.Id,
            "drafts folder listed");
    }
}

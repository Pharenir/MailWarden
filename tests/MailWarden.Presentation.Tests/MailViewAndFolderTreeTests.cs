namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a cases 21 and 22 (D8): <see cref="MailView"/> and <see cref="FolderTree"/>. Pure, synchronous logic.</summary>
[Trait("Category", "feature:ui")]
public sealed class MailViewAndFolderTreeTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static MailFolder Folder(string fullName, long id = 1, FolderRole role = FolderRole.None) =>
        new(id, AccountId, fullName, role, 1000);

    // ---- Case 21 -------------------------------------------------------------------------------------------

    // Case 21
    [Fact]
    public void Group_view_has_only_the_role()
    {
        MailView view = MailView.Group(FolderRole.Inbox);

        Assert.Equal(FolderRole.Inbox, view.Role);
        Assert.Null(view.AccountId);
        Assert.Null(view.FolderId);
    }

    // Case 21
    [Fact]
    public void Group_for_account_view_has_role_and_account()
    {
        MailView view = MailView.GroupForAccount(FolderRole.Junk, AccountId);

        Assert.Equal(FolderRole.Junk, view.Role);
        Assert.Equal(AccountId, view.AccountId);
        Assert.Null(view.FolderId);
    }

    // Case 21
    [Fact]
    public void Folder_view_has_account_and_folder_but_no_role()
    {
        MailView view = MailView.Folder(AccountId, 42);

        Assert.Null(view.Role);
        Assert.Equal(AccountId, view.AccountId);
        Assert.Equal(42L, view.FolderId);
    }

    // Case 21
    [Fact]
    public void Views_compare_by_value()
    {
        Assert.Equal(MailView.Group(FolderRole.Inbox), MailView.Group(FolderRole.Inbox));
        Assert.NotEqual(MailView.Group(FolderRole.Inbox), MailView.Group(FolderRole.Sent));
        Assert.NotEqual(MailView.Group(FolderRole.Inbox), MailView.GroupForAccount(FolderRole.Inbox, AccountId));
        Assert.Equal(MailView.Folder(AccountId, 7), MailView.Folder(AccountId, 7));
        Assert.NotEqual(MailView.Folder(AccountId, 7), MailView.Folder(AccountId, 8));
    }

    // Case 21
    [Fact]
    public void ToQuery_passes_filter_limit_and_offset_through()
    {
        Assert.Equal(new MessageQuery(FolderRole.Inbox, null, null, 100, 200), MailView.Group(FolderRole.Inbox).ToQuery(100, 200));
        Assert.Equal(
            new MessageQuery(FolderRole.Junk, AccountId, null, 25, 0),
            MailView.GroupForAccount(FolderRole.Junk, AccountId).ToQuery(25, 0));
        Assert.Equal(new MessageQuery(null, AccountId, 42, 7, 3), MailView.Folder(AccountId, 42).ToQuery(7, 3));
    }

    // Case 21
    [Fact]
    public void Group_roles_are_the_six_group_views_in_the_documented_order()
    {
        Assert.Equal(
            new[] { FolderRole.Inbox, FolderRole.Sent, FolderRole.Outbox, FolderRole.Screener, FolderRole.Junk, FolderRole.Quarantine },
            MailView.GroupRoles.ToArray());
    }

    // ---- Case 22 -------------------------------------------------------------------------------------------

    private static FolderTreeNode Node(IReadOnlyList<FolderTreeNode> nodes, string name) =>
        Assert.Single(nodes, node => node.Name == name);

    // Case 22
    [Fact]
    public void Empty_input_gives_an_empty_tree()
    {
        Assert.Empty(FolderTree.Build([]));
    }

    // Case 22
    [Fact]
    public void Hierarchy_is_split_at_slash_and_missing_parents_are_nodes_without_folder()
    {
        MailFolder a = Folder("A", 1);
        MailFolder abc = Folder("A/B/C", 2);
        MailFolder z = Folder("Z", 3);
        MailFolder xy = Folder("X/Y", 4);

        IReadOnlyList<FolderTreeNode> roots = FolderTree.Build([abc, z, a, xy]);

        Assert.Equal(new[] { "A", "X", "Z" }, roots.Select(node => node.Name).ToArray());
        FolderTreeNode nodeA = Node(roots, "A");
        Assert.Equal(a, nodeA.Folder);
        FolderTreeNode nodeB = Assert.Single(nodeA.Children);
        Assert.Equal("B", nodeB.Name);
        Assert.Null(nodeB.Folder);
        FolderTreeNode nodeC = Assert.Single(nodeB.Children);
        Assert.Equal("C", nodeC.Name);
        Assert.Equal(abc, nodeC.Folder);
        Assert.Empty(nodeC.Children);
        FolderTreeNode nodeX = Node(roots, "X");
        Assert.Null(nodeX.Folder);
        FolderTreeNode nodeY = Assert.Single(nodeX.Children);
        Assert.Equal(xy, nodeY.Folder);
        Assert.Equal(z, Node(roots, "Z").Folder);
        Assert.Empty(Node(roots, "Z").Children);
    }

    // Case 22
    [Fact]
    public void Siblings_are_ordered_ignoring_case_and_independent_of_the_input_order()
    {
        string[] names = ["beta", "Alpha", "Gamma", "alpha2", "Delta"];
        MailFolder[] folders = [.. names.Select((name, index) => Folder(name, index + 1))];

        IReadOnlyList<FolderTreeNode> forward = FolderTree.Build(folders);
        IReadOnlyList<FolderTreeNode> reversed = FolderTree.Build(folders.Reverse());

        string[] expected = ["Alpha", "alpha2", "beta", "Delta", "Gamma"];
        Assert.Equal(expected, forward.Select(node => node.Name).ToArray());
        Assert.Equal(expected, reversed.Select(node => node.Name).ToArray());
    }

    // Case 22
    [Fact]
    public void Children_are_ordered_ignoring_case_too()
    {
        IReadOnlyList<FolderTreeNode> roots = FolderTree.Build([Folder("P/zeta", 1), Folder("P/Alpha", 2), Folder("P/beta", 3)]);

        FolderTreeNode parent = Assert.Single(roots);
        Assert.Null(parent.Folder);
        Assert.Equal(new[] { "Alpha", "beta", "zeta" }, parent.Children.Select(node => node.Name).ToArray());
    }

    // Case 22
    [Fact]
    public void Node_names_are_cleaned_of_bidi_and_control_characters_but_the_folder_keeps_its_name()
    {
        MailFolder dirty = Folder("Pro\u202Eject\u0007s/Sub\u2066Folder", 5);

        IReadOnlyList<FolderTreeNode> roots = FolderTree.Build([dirty]);

        FolderTreeNode parent = Assert.Single(roots);
        Assert.Equal("Projects", parent.Name);
        FolderTreeNode child = Assert.Single(parent.Children);
        Assert.Equal("SubFolder", child.Name);
        Assert.Equal(dirty, child.Folder);
    }

    // Case 22: only '/' splits; other delimiters stay part of the name.
    [Fact]
    public void Dot_and_backslash_do_not_split_the_name()
    {
        IReadOnlyList<FolderTreeNode> roots = FolderTree.Build([Folder("INBOX.Sub", 1), Folder("a\\b", 2)]);

        Assert.Equal(new[] { "a\\b", "INBOX.Sub" }, roots.Select(node => node.Name).ToArray());
        Assert.All(roots, node => Assert.Empty(node.Children));
    }

    // Case 22: a real folder and a missing-parent placeholder merge instead of duplicating.
    [Fact]
    public void Parent_that_exists_as_a_folder_is_used_once_even_when_a_child_comes_first()
    {
        MailFolder parent = Folder("Archive", 1);
        MailFolder child = Folder("Archive/2025", 2);

        IReadOnlyList<FolderTreeNode> roots = FolderTree.Build([child, parent]);

        FolderTreeNode node = Assert.Single(roots);
        Assert.Equal(parent, node.Folder);
        Assert.Equal(child, Assert.Single(node.Children).Folder);
    }
}

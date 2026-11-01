using System.Collections.ObjectModel;
using MailWarden.Core.Model;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Logging;

namespace MailWarden.Presentation;

/// <summary>
/// One entry of the navigation tree: a group view, an account node, a role view of an account or a folder.
/// </summary>
public sealed class NavigationItem : ObservableObject
{
    private int unreadCount;
    private string? statusText;

    internal NavigationItem(string label, MailView? view, Guid? accountId, IReadOnlyList<NavigationItem> children)
    {
        Label = label;
        View = view;
        AccountId = accountId;
        Children = children;
    }

    /// <summary>Text of the entry, cleaned for display.</summary>
    public string Label { get; }

    /// <summary>The view it selects; null when not selectable (account node, tree parent).</summary>
    public MailView? View { get; }

    /// <summary>Number of unread messages of the view; 0 when <see cref="View"/> is null.</summary>
    public int UnreadCount
    {
        get => unreadCount;
        internal set => SetProperty(ref unreadCount, value);
    }

    /// <summary>Account of the entry; null for group views over all accounts.</summary>
    public Guid? AccountId { get; }

    /// <summary>Account nodes: fixed text for the sync state; otherwise null.</summary>
    public string? StatusText
    {
        get => statusText;
        internal set => SetProperty(ref statusText, value);
    }

    /// <summary>Child entries.</summary>
    public IReadOnlyList<NavigationItem> Children { get; }
}

/// <summary>
/// The navigation tree on the left: the group views over all accounts, then one node per account.
/// </summary>
public sealed class NavigationViewModel : ObservableObject
{
    private readonly IMailboxReadModel readModel;
    private readonly IMailboxController controller;
    private readonly ICriticalLog log;
    private readonly ShellBanner banner;
    private readonly Dictionary<Guid, NavigationItem> accountItems = [];
    private NavigationItem? selectedItem;
    private int refreshGeneration;

    internal NavigationViewModel(
        IMailboxReadModel readModel,
        IMailboxController controller,
        ICriticalLog log,
        ShellBanner banner)
    {
        this.readModel = readModel;
        this.controller = controller;
        this.log = log;
        this.banner = banner;
    }

    /// <summary>Top-level entries.</summary>
    public ObservableCollection<NavigationItem> Items { get; } = [];

    /// <summary>The selected entry. Selecting an entry with <see cref="NavigationItem.View"/> null is ignored.</summary>
    public NavigationItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (value?.View is not null)
            {
                SetProperty(ref selectedItem, value);
            }
        }
    }

    /// <summary>Ids of the accounts currently shown.</summary>
    internal IReadOnlyCollection<Guid> AccountIds => accountItems.Keys;

    /// <summary>
    /// Reads accounts, folders and unread counts and updates the tree. An unchanged structure is updated in place, so
    /// expanded nodes and the selection stay. Selects the group Inbox when nothing is selected. A failure is logged and
    /// reported in the banner; an older pending refresh never overwrites a newer one.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the tree is updated.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        int generation = ++refreshGeneration;
        try
        {
            IReadOnlyList<Account> accounts = await readModel.ListAccountsAsync(ct);
            List<NavigationItem> newItems = [.. CreateGroupItems()];
            var newAccountItems = new Dictionary<Guid, NavigationItem>();
            foreach (Account account in accounts)
            {
                IReadOnlyList<MailFolder> folders = await readModel.ListFoldersAsync(account.Id, ct);
                NavigationItem accountItem = CreateAccountItem(account, folders);
                newAccountItems.Add(account.Id, accountItem);
                newItems.Add(accountItem);
            }

            Dictionary<MailView, int> unreadCounts = await ReadUnreadCountsAsync(newItems, ct);
            if (generation != refreshGeneration)
            {
                return;
            }

            ApplyTree(newItems, newAccountItems, unreadCounts);
            banner.Clear(ShellBanner.BannerSource.Navigation);
            if (accounts.Count == 0)
            {
                banner.Set(ShellBanner.BannerSource.NoAccounts, UiText.BannerNoAccounts);
            }
            else
            {
                banner.Clear(ShellBanner.BannerSource.NoAccounts);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            if (generation == refreshGeneration)
            {
                log.Write(LogLevels.Critical, "NavigationViewModel.RefreshAsync", $"Reading the navigation failed: {exception.GetType().Name}");
                banner.Set(ShellBanner.BannerSource.Navigation, UiText.BannerNavigationFailed);
                if (Items.Count == 0)
                {
                    ApplyTree(CreateGroupItems(), [], []);
                }
            }
        }
    }

    /// <summary>Shows the status on its account node.</summary>
    /// <param name="status">New status.</param>
    /// <returns>False when the account is unknown; such a status is ignored.</returns>
    internal bool ApplyStatus(AccountSyncStatus status)
    {
        if (!accountItems.TryGetValue(status.AccountId, out NavigationItem? accountItem))
        {
            return false;
        }

        accountItem.StatusText = UiText.AccountStatus(status);
        return true;
    }

    /// <summary>The cleaned display name of an account, else its cleaned address.</summary>
    /// <param name="account">Account.</param>
    /// <returns>The label.</returns>
    internal static string AccountLabel(Account account)
    {
        string name = SafeDisplayText.Clean(account.DisplayName);
        return string.IsNullOrWhiteSpace(name) ? SafeDisplayText.Clean(account.EmailAddress) : name;
    }

    private static List<NavigationItem> CreateGroupItems()
    {
        return MailView.GroupRoles
            .Select(role => new NavigationItem(UiText.RoleLabel(role), MailView.Group(role), null, []))
            .ToList();
    }

    private static NavigationItem CreateAccountItem(Account account, IReadOnlyList<MailFolder> folders)
    {
        List<NavigationItem> children = MailView.GroupRoles
            .Select(role => new NavigationItem(UiText.RoleLabel(role), MailView.GroupForAccount(role, account.Id), account.Id, []))
            .ToList();

        // Folders with a group role are reached through the role views above.
        IEnumerable<MailFolder> otherFolders = folders.Where(folder => !MailView.GroupRoles.Contains(folder.Role));
        children.AddRange(CreateFolderItems(FolderTree.Build(otherFolders), account.Id));
        return new NavigationItem(AccountLabel(account), null, account.Id, children);
    }

    private static List<NavigationItem> CreateFolderItems(IReadOnlyList<FolderTreeNode> nodes, Guid accountId)
    {
        return nodes
            .Select(node => new NavigationItem(
                node.Name,
                node.Folder is null ? null : MailView.Folder(accountId, node.Folder.Id),
                accountId,
                CreateFolderItems(node.Children, accountId)))
            .ToList();
    }

    private static IEnumerable<NavigationItem> Flatten(IEnumerable<NavigationItem> items)
    {
        foreach (NavigationItem item in items)
        {
            yield return item;
            foreach (NavigationItem child in Flatten(item.Children))
            {
                yield return child;
            }
        }
    }

    private static string DescribeStructure(IEnumerable<NavigationItem> items)
    {
        return string.Join('\n', Flatten(items).Select(item => $"{item.Label}|{item.View}|{item.AccountId}|{item.Children.Count}"));
    }

    private async Task<Dictionary<MailView, int>> ReadUnreadCountsAsync(List<NavigationItem> items, CancellationToken ct)
    {
        var counts = new Dictionary<MailView, int>();
        foreach (NavigationItem item in Flatten(items))
        {
            if (item.View is MailView view)
            {
                counts[view] = await readModel.CountUnreadAsync(view.ToQuery(1, 0), ct);
            }
        }

        return counts;
    }

    // Keeps the existing item objects when the structure is unchanged, because the tree control holds on to them.
    private void ApplyTree(
        List<NavigationItem> newItems,
        Dictionary<Guid, NavigationItem> newAccountItems,
        Dictionary<MailView, int> unreadCounts)
    {
        if (DescribeStructure(Items) != DescribeStructure(newItems) || Items.Count == 0)
        {
            Items.Clear();
            foreach (NavigationItem item in newItems)
            {
                Items.Add(item);
            }

            accountItems.Clear();
            foreach ((Guid accountId, NavigationItem accountItem) in newAccountItems)
            {
                accountItems.Add(accountId, accountItem);
            }
        }

        foreach (NavigationItem item in Flatten(Items))
        {
            if (item.View is MailView view)
            {
                item.UnreadCount = unreadCounts.GetValueOrDefault(view);
            }
        }

        ApplyCurrentStatus();
        RestoreSelection();
    }

    // Only statuses of listed accounts are used: the service keeps the status of removed accounts.
    private void ApplyCurrentStatus()
    {
        Dictionary<Guid, AccountSyncStatus> statuses = controller.CurrentStatus
            .GroupBy(status => status.AccountId)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach ((Guid accountId, NavigationItem accountItem) in accountItems)
        {
            accountItem.StatusText = UiText.AccountStatus(statuses.GetValueOrDefault(accountId));
        }
    }

    // The same view in the new tree, otherwise the group Inbox (default selection, or the selected folder is gone).
    private void RestoreSelection()
    {
        List<NavigationItem> allItems = Flatten(Items).ToList();
        NavigationItem? sameView = selectedItem is null
            ? null
            : allItems.FirstOrDefault(item => item.View == selectedItem.View);
        SelectedItem = sameView ?? allItems.First(item => item.View == MailView.Group(FolderRole.Inbox));
    }
}

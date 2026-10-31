using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class FolderRoleResolverTests
{
    private const MailProvider Microsoft = MailProvider.Microsoft;
    private const MailProvider Google = MailProvider.Google;

    private static FolderClassification Resolve(MailProvider provider, string name, ServerFolderFlags flags = ServerFolderFlags.None) =>
        FolderRoleResolver.Resolve(provider, name, '/', flags);

    // Case 8
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("INBOX", ServerFolderFlags.Inbox, FolderRole.Inbox, false)]
    [InlineData("Sent", ServerFolderFlags.Sent, FolderRole.Sent, false)]
    [InlineData("Junk", ServerFolderFlags.Junk, FolderRole.Junk, false)]
    [InlineData("Drafts", ServerFolderFlags.Drafts, FolderRole.Drafts, false)]
    [InlineData("Deleted", ServerFolderFlags.Trash, FolderRole.Trash, false)]
    [InlineData("Archive", ServerFolderFlags.None, FolderRole.Archive, false)]
    [InlineData("Archive/Steuer", ServerFolderFlags.None, FolderRole.None, false)]
    [InlineData("Notes", ServerFolderFlags.None, FolderRole.None, true)]
    [InlineData("Outbox", ServerFolderFlags.None, FolderRole.None, true)]
    [InlineData("Scheduled", ServerFolderFlags.None, FolderRole.None, true)]
    [InlineData("Synchronisierungsprobleme", ServerFolderFlags.None, FolderRole.None, true)]
    [InlineData("Synchronisierungsprobleme/Konflikte", ServerFolderFlags.None, FolderRole.None, true)]
    public void Microsoft_list_from_AP00(string name, ServerFolderFlags flags, FolderRole role, bool hidden)
    {
        FolderClassification result = Resolve(Microsoft, name, flags);

        Assert.Equal(new FolderClassification(role, hidden), result);
    }

    // Case 9
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("INBOX", ServerFolderFlags.Inbox, FolderRole.Inbox, false)]
    [InlineData("[Gmail]", ServerFolderFlags.NonExistent | ServerFolderFlags.NoSelect, FolderRole.None, true)]
    [InlineData("[Gmail]", ServerFolderFlags.NonExistent, FolderRole.None, true)]
    [InlineData("[Gmail]/All Mail", ServerFolderFlags.All, FolderRole.All, true)]
    [InlineData("[Gmail]/Spam", ServerFolderFlags.Junk, FolderRole.Junk, false)]
    [InlineData("[Gmail]/Bin", ServerFolderFlags.Trash, FolderRole.Trash, false)]
    [InlineData("[Gmail]/Sent Mail", ServerFolderFlags.Sent, FolderRole.Sent, false)]
    [InlineData("[Gmail]/Drafts", ServerFolderFlags.Drafts, FolderRole.Drafts, false)]
    [InlineData("[Gmail]/Important", ServerFolderFlags.Important, FolderRole.None, true)]
    [InlineData("[Gmail]/Starred", ServerFolderFlags.Flagged, FolderRole.None, true)]
    [InlineData("Junk-E-Mail", ServerFolderFlags.None, FolderRole.None, false)]
    [InlineData("Auto", ServerFolderFlags.None, FolderRole.None, false)]
    public void Google_list_from_AP00(string name, ServerFolderFlags flags, FolderRole role, bool hidden)
    {
        FolderClassification result = Resolve(Google, name, flags);

        Assert.Equal(new FolderClassification(role, hidden), result);
    }

    // Case 10
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("Outbox")]
    [InlineData("OUTBOX")]
    public void Server_outbox_is_never_the_Outbox_role(string name)
    {
        Assert.NotEqual(FolderRole.Outbox, Resolve(Microsoft, name).Role);
        Assert.NotEqual(FolderRole.Outbox, Resolve(Google, name).Role);
    }

    // Case 11
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(MailProvider.Microsoft, "Screener", FolderRole.Screener)]
    [InlineData(MailProvider.Google, "Screener", FolderRole.Screener)]
    [InlineData(MailProvider.Microsoft, "Quarantine", FolderRole.Quarantine)]
    [InlineData(MailProvider.Google, "Quarantine", FolderRole.Quarantine)]
    [InlineData(MailProvider.Microsoft, "Archive/Screener", FolderRole.None)]
    [InlineData(MailProvider.Google, "Archive/Screener", FolderRole.None)]
    [InlineData(MailProvider.Google, "Archive/Quarantine", FolderRole.None)]
    public void App_owned_folders_are_recognised_only_at_top_level(MailProvider provider, string name, FolderRole expected)
    {
        Assert.Equal(expected, Resolve(provider, name).Role);
    }

    // Case 12
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("inbox")]
    [InlineData("Inbox")]
    public void Name_inbox_is_case_insensitive_without_flags(string name)
    {
        Assert.Equal(new FolderClassification(FolderRole.Inbox, false), Resolve(Google, name));
    }

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("Spam")]
    [InlineData("Sent")]
    [InlineData("Trash")]
    public void Role_names_without_flags_stay_None_on_google(string name)
    {
        Assert.Equal(new FolderClassification(FolderRole.None, false), Resolve(Google, name));
    }

    // Rule 5 is Microsoft only
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("Archive")]
    [InlineData("Notes")]
    public void Microsoft_name_rules_do_not_apply_to_google(string name)
    {
        Assert.Equal(new FolderClassification(FolderRole.None, false), Resolve(Google, name));
    }

    // Rule 1 precedes rule 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void NoSelect_wins_over_role_flags()
    {
        Assert.Equal(
            new FolderClassification(FolderRole.None, true),
            Resolve(Microsoft, "Weird", ServerFolderFlags.NoSelect | ServerFolderFlags.Sent));
    }

    // Case 13
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_name_throws_ArgumentException(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(() => FolderRoleResolver.Resolve(Microsoft, name!, '/', ServerFolderFlags.None));
    }
}

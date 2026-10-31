using MailKit;
using MailWarden.Core.Accounts;
using MailWarden.Infrastructure.Providers;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class MailKitFolderFlagsTests
{
    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void Combined_attributes_map_to_combined_flags()
    {
        ServerFolderFlags result = MailKitFolderFlags.From(FolderAttributes.Inbox | FolderAttributes.Junk | FolderAttributes.NoSelect);

        Assert.Equal(ServerFolderFlags.Inbox | ServerFolderFlags.Junk | ServerFolderFlags.NoSelect, result);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void HasChildren_alone_maps_to_None()
    {
        Assert.Equal(ServerFolderFlags.None, MailKitFolderFlags.From(FolderAttributes.HasChildren));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public void No_attributes_map_to_None()
    {
        Assert.Equal(ServerFolderFlags.None, MailKitFolderFlags.From(FolderAttributes.None));
    }

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(FolderAttributes.Inbox, ServerFolderFlags.Inbox)]
    [InlineData(FolderAttributes.Sent, ServerFolderFlags.Sent)]
    [InlineData(FolderAttributes.Drafts, ServerFolderFlags.Drafts)]
    [InlineData(FolderAttributes.Junk, ServerFolderFlags.Junk)]
    [InlineData(FolderAttributes.Trash, ServerFolderFlags.Trash)]
    [InlineData(FolderAttributes.Archive, ServerFolderFlags.Archive)]
    [InlineData(FolderAttributes.All, ServerFolderFlags.All)]
    [InlineData(FolderAttributes.Flagged, ServerFolderFlags.Flagged)]
    [InlineData(FolderAttributes.Important, ServerFolderFlags.Important)]
    [InlineData(FolderAttributes.NoSelect, ServerFolderFlags.NoSelect)]
    [InlineData(FolderAttributes.NonExistent, ServerFolderFlags.NonExistent)]
    public void Each_listed_attribute_maps_to_its_flag(FolderAttributes attribute, ServerFolderFlags expected)
    {
        Assert.Equal(expected, MailKitFolderFlags.From(attribute));
    }
}

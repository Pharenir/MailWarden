using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Mime;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>
/// AP-04a case 49 (K2), sync part: a message with extreme MIME nesting is stored by a sync pass and can be read
/// afterwards. Own class on purpose: a crash of the test host can be reproduced by running just this class.
/// </summary>
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:ui")]
public sealed class SyncDeepNestingTests
{
    private const string Inbox = "INBOX";

    private static async Task<(SyncHarness Harness, FakeMailFolder Folder)> StartAsync()
    {
        SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder folder = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox, condStore: true);
        return (harness, folder);
    }

    // Case 49
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("multipart")]
    [InlineData("rfc822")]
    public async Task Deeply_nested_message_is_stored_and_readable(string kind)
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        byte[] deep = kind == "multipart" ? DeepMimeEml.NestedMultipart(1_023) : DeepMimeEml.NestedRfc822(1_023);
        server.AddMessage(SyncTestEml.Plain("control_before", "control body one", "<msg_before@example.test>"));
        server.AddMessage(deep);
        server.AddMessage(SyncTestEml.Plain("control_after", "control body two", "<msg_after@example.test>"));

        SyncReport report = await harness.SyncAsync();

        IReadOnlyList<MailMessage> stored = await harness.MessagesOfAsync(Inbox);
        Assert.Equal(new uint[] { 1, 2, 3 }, stored.Select(message => message.Uid).ToArray());
        MailMessage deepRow = stored[1];
        Assert.Equal(DeepMimeEml.Subject, deepRow.Subject);
        Assert.NotNull(deepRow.EmlPath);
        Assert.True(harness.Eml.Exists(deepRow.EmlPath));
        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal(3, result.Added);
        Assert.Null(result.Error);

        await using Stream eml = await harness.Eml.OpenReadAsync(deepRow.EmlPath);
        MailContent content = await MailContentReader.ReadAsync(eml);
        Assert.Equal(DeepMimeEml.Subject, content.Subject);
    }

    // Case 49 positive control: the neighbours of the deep message are stored normally and are readable.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Messages_around_a_deep_one_are_stored_with_their_text()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(DeepMimeEml.NestedMultipart(1_023));
        server.AddMessage(SyncTestEml.Plain("control_after", "searchable_zebraword", "<msg_after@example.test>"));

        await harness.SyncAsync();

        Assert.Equal(new[] { DeepMimeEml.Subject, "control_after" }, (await harness.MessagesOfAsync(Inbox)).Select(message => message.Subject).ToArray());
        Assert.Single(await harness.Storage.Messages.SearchAsync("zebraword"));
    }
}

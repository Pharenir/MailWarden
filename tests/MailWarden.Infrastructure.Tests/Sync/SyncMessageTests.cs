using System.Text;
using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a rules R5 (new messages), R6 (row mapping) and cases 4, 5, 6, 12, 13, 14, 15.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncMessageTests
{
    private const string Inbox = "INBOX";

    private static async Task<(SyncHarness Harness, FakeMailFolder Folder)> StartAsync(int initialSyncDays = 0)
    {
        SyncHarness harness = await SyncHarness.CreateAsync(initialSyncDays);
        FakeMailFolder folder = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox, condStore: true);
        return (harness, folder);
    }

    // Case 4
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    [Trait("Category", "smoke")]
    public async Task First_sync_stores_three_messages_with_files_search_entries_and_state()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("first_subject", "alpha quokkaword omega", "<msg_1@example.test>"));
        server.AddMessage(SyncTestEml.Plain("second_subject", "wombatword", "<msg_2@example.test>"));
        uint lastUid = server.AddMessage(SyncTestEml.Plain("third_subject", "zebraword", "<msg_3@example.test>"));

        SyncReport report = await harness.SyncAsync();

        IReadOnlyList<MailMessage> stored = await harness.MessagesOfAsync(Inbox);
        Assert.Equal(new uint[] { 1, 2, 3 }, stored.Select(message => message.Uid).ToArray());
        Assert.Equal(new[] { "first_subject", "second_subject", "third_subject" }, stored.Select(message => message.Subject).ToArray());
        Assert.Equal(3, harness.EmlFileCount());
        Assert.All(stored, message => Assert.True(harness.Eml.Exists(message.EmlPath!)));
        MailMessage found = Assert.Single(await harness.Storage.Messages.SearchAsync("quokkaword"));
        Assert.Equal(1u, found.Uid);
        FolderSyncState? state = await harness.StateOfAsync(Inbox);
        Assert.NotNull(state);
        Assert.Equal(lastUid, state.HighestSyncedUid);
        Assert.Equal(server.HighestModSeq, state.HighestModSeq);
        Assert.Equal(SyncHarness.FixedNow, state.LastSyncedAt!.Value);
        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.FlagsUpdated);
        Assert.Equal(0, result.Deleted);
        Assert.False(result.UidValidityReset);
        Assert.Null(result.Error);
    }

    // Case 4 / R6: every field of the row
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Row_fields_are_mapped_from_headers_fetch_result_and_file_path()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        byte[] raw = SyncTestEml.Plain("mapped_subject", "mapped body text", "<msg_map@example.test>");
        server.AddMessage(
            raw,
            MessageFlags.Seen | MessageFlags.Flagged,
            new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
            size: 4321);

        await harness.SyncAsync();

        MailFolder folder = await harness.GetFolderAsync(Inbox);
        MailMessage row = Assert.Single(await harness.MessagesOfAsync(Inbox));
        Assert.Equal(harness.Account.Id, row.AccountId);
        Assert.Equal(folder.Id, row.FolderId);
        Assert.Equal(1u, row.Uid);
        Assert.Equal("msg_map@example.test", row.MessageId);
        Assert.Equal("mapped_subject", row.Subject);
        Assert.Equal("sender_a@example.test", row.FromAddress);
        Assert.Equal("Sender A", row.FromName);
        Assert.Equal(SyncTestEml.DefaultDate, row.Date);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, row.Flags);
        Assert.False(row.HasAttachments);
        Assert.Equal(4321L, row.Size);
        Assert.Equal(harness.Eml.RelativePathFor(harness.Account.Id, folder.Id, 1), row.EmlPath);
        Assert.Equal("mapped body text", row.TextPreview);
        Assert.Equal(raw, await harness.ReadEmlAsync(row));
    }

    // R6: HasAttachments
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_with_attachment_sets_has_attachments()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.WithAttachment("attached_subject", "see attachment"));
        server.AddMessage(SyncTestEml.Plain("plain_subject", "no attachment"));

        await harness.SyncAsync();

        IReadOnlyList<MailMessage> rows = await harness.MessagesOfAsync(Inbox);
        Assert.True(rows[0].HasAttachments);
        Assert.False(rows[1].HasAttachments);
    }

    // R6: first mailbox of From, empty fields when missing
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task From_uses_the_first_mailbox_and_missing_headers_map_to_empty_strings()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("multi_from", "body", from: "First <first_a@example.test>, Second <second_a@example.test>"));
        server.AddMessage(SyncTestEml.Plain(null, "body", messageId: null, from: null));

        await harness.SyncAsync();

        IReadOnlyList<MailMessage> rows = await harness.MessagesOfAsync(Inbox);
        Assert.Equal("first_a@example.test", rows[0].FromAddress);
        Assert.Equal("First", rows[0].FromName);
        Assert.Equal(string.Empty, rows[1].Subject);
        Assert.Equal(string.Empty, rows[1].FromAddress);
        Assert.Equal(string.Empty, rows[1].FromName);
        Assert.Null(rows[1].MessageId);
    }

    // R6: preview <= 200 chars, full-text index covers the long body
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Preview_is_cut_to_200_chars_while_the_search_index_holds_the_full_body()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        string body = string.Concat(Enumerable.Repeat("filler ", 100)) + "lateuniqueword";
        server.AddMessage(SyncTestEml.Plain("long_subject", body));

        await harness.SyncAsync();

        MailMessage row = Assert.Single(await harness.MessagesOfAsync(Inbox));
        Assert.InRange(row.TextPreview.Length, 1, 200);
        Assert.StartsWith("filler filler", row.TextPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("lateuniqueword", row.TextPreview, StringComparison.Ordinal);
        MailMessage found = Assert.Single(await harness.Storage.Messages.SearchAsync("lateuniqueword"));
        Assert.Equal(row.Id, found.Id);
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Initial_window_limits_the_first_sync_and_older_mail_is_never_fetched_later()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(initialSyncDays: 30);
        await using SyncHarness _ = harness;
        uint oldUid = server.AddMessage(SyncTestEml.Plain("old_subject", "oldbody"), internalDate: SyncHarness.FixedNow.AddDays(-60));
        uint recentUid = server.AddMessage(SyncTestEml.Plain("recent_subject", "recentbody"), internalDate: SyncHarness.FixedNow.AddDays(-10));

        SyncReport first = await harness.SyncAsync();

        Assert.Equal(new[] { recentUid }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(new[] { (Inbox, recentUid) }, harness.Server.Fetches.ToArray());
        Assert.Equal(1, Assert.Single(first.Folders).Added);
        FolderSyncState? state = await harness.StateOfAsync(Inbox);
        Assert.Equal(recentUid, state!.HighestSyncedUid);
        Assert.True(oldUid < state.HighestSyncedUid);

        harness.Server.ClearLog();
        SyncReport second = await harness.SyncAsync();

        Assert.Empty(harness.Server.Fetches);
        Assert.Equal(new[] { recentUid }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(0, Assert.Single(second.Folders).Added);
    }

    // Case 5 (R5): the window search is requested with a SINCE date of "today - days"
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Initial_window_search_uses_today_minus_the_configured_days()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(initialSyncDays: 30);
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("recent_subject", "recentbody"), internalDate: SyncHarness.FixedNow.AddDays(-1));

        await harness.SyncAsync();

        DateTimeOffset?[] searches = harness.Server.SearchSinceArguments.ToArray();
        Assert.Contains(searches, since => since is not null && since.Value.Date == SyncHarness.FixedNow.AddDays(-30).Date);
        Assert.Contains(searches, since => since is null);
    }

    // Case 5 (R5): the boundary day itself is inside the window
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_on_the_first_day_of_the_window_is_included()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(initialSyncDays: 30);
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("day_before", "x"), internalDate: SyncHarness.FixedNow.AddDays(-31));
        uint boundaryUid = server.AddMessage(SyncTestEml.Plain("boundary_day", "x"), internalDate: SyncHarness.FixedNow.AddDays(-30));

        await harness.SyncAsync();

        Assert.Equal(new[] { boundaryUid }, await harness.StoredUidsAsync(Inbox));
    }

    // Case 5 (R5): InitialSyncDays 0 means everything
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Zero_initial_days_syncs_even_very_old_mail()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync(initialSyncDays: 0);
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("ancient_subject", "x"), internalDate: SyncHarness.FixedNow.AddYears(-8));

        await harness.SyncAsync();

        Assert.Single(await harness.StoredUidsAsync(Inbox));
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_sync_fetches_only_the_new_uid()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("first_subject", "x"));
        server.AddMessage(SyncTestEml.Plain("second_subject", "x"));
        await harness.SyncAsync();
        uint newUid = server.AddMessage(SyncTestEml.Plain("new_subject", "newbodyword"));
        harness.Server.ClearLog();

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(new[] { (Inbox, newUid) }, harness.Server.Fetches.ToArray());
        Assert.Equal(new uint[] { 1, 2, newUid }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(1, Assert.Single(report.Folders).Added);
        Assert.Equal(newUid, (await harness.StateOfAsync(Inbox))!.HighestSyncedUid);
        Assert.Single(await harness.Storage.Messages.SearchAsync("newbodyword"));
    }

    // Case 6 (supporting): nothing changed -> nothing fetched, counts zero
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_sync_without_changes_fetches_nothing_and_reports_zero()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("first_subject", "x"));
        server.AddMessage(SyncTestEml.Plain("second_subject", "x"));
        await harness.SyncAsync();
        harness.Server.ClearLog();

        SyncReport report = await harness.SyncAsync();

        Assert.Empty(harness.Server.Fetches);
        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Equal((0, 0, 0), (result.Added, result.FlagsUpdated, result.Deleted));
        Assert.Null(result.Error);
        Assert.Equal(2, harness.EmlFileCount());
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Fetch_returning_null_skips_that_uid_without_error()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        server.AddMessage(SyncTestEml.Plain("one_subject", "x"));
        uint vanishedUid = server.AddMessage(SyncTestEml.Plain("two_subject", "x"));
        server.AddMessage(SyncTestEml.Plain("three_subject", "x"));
        server.NullFetchUids.Add(vanishedUid);

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(new uint[] { 1, 3 }, await harness.StoredUidsAsync(Inbox));
        FolderSyncResult result = Assert.Single(report.Folders);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Added);
        Assert.Equal(2, harness.EmlFileCount());
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Message_without_date_header_uses_the_internal_date()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        var internalDate = new DateTimeOffset(2026, 9, 9, 7, 30, 0, TimeSpan.Zero);
        server.AddMessage(SyncTestEml.Plain("undated_subject", "body", date: null), internalDate: internalDate);

        await harness.SyncAsync();

        MailMessage row = Assert.Single(await harness.MessagesOfAsync(Inbox));
        Assert.Equal(internalDate, row.Date);
    }

    // Case 14
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Html_only_message_yields_plain_text_without_tags_or_script_content()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        const string html =
            "<html><head><style>.hidden{color:redstylevalue}</style></head><body>" +
            "<script>var scriptsecretvalue = 1;</script><p>Hello <b>htmlwombatword</b> &amp; friends</p></body></html>";
        server.AddMessage(SyncTestEml.HtmlOnly("html_subject", html));

        await harness.SyncAsync();

        MailMessage row = Assert.Single(await harness.MessagesOfAsync(Inbox));
        Assert.Contains("htmlwombatword", row.TextPreview, StringComparison.Ordinal);
        Assert.Contains("friends", row.TextPreview, StringComparison.Ordinal);
        Assert.DoesNotContain('<', row.TextPreview);
        Assert.DoesNotContain('>', row.TextPreview);
        Assert.DoesNotContain("scriptsecretvalue", row.TextPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("redstylevalue", row.TextPreview, StringComparison.Ordinal);
        Assert.Single(await harness.Storage.Messages.SearchAsync("htmlwombatword"));
        Assert.Empty(await harness.Storage.Messages.SearchAsync("scriptsecretvalue"));
        Assert.Empty(await harness.Storage.Messages.SearchAsync("redstylevalue"));
    }

    // Case 15
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unparseable_bytes_are_stored_with_empty_header_fields_and_the_file_is_kept()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        var internalDate = new DateTimeOffset(2026, 8, 8, 6, 0, 0, TimeSpan.Zero);
        byte[] garbage = SyncTestEml.Garbage();
        server.AddMessage(garbage, internalDate: internalDate);

        SyncReport report = await harness.SyncAsync();

        MailMessage row = Assert.Single(await harness.MessagesOfAsync(Inbox));
        Assert.Equal(string.Empty, row.Subject);
        Assert.Equal(string.Empty, row.FromAddress);
        Assert.Equal(string.Empty, row.FromName);
        Assert.Equal(internalDate, row.Date);
        Assert.NotNull(row.EmlPath);
        Assert.Equal(garbage, await harness.ReadEmlAsync(row));
        Assert.Null(Assert.Single(report.Folders).Error);
    }

    // Case 15 (supporting): bytes that are not even text
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Binary_noise_is_stored_and_does_not_abort_the_folder()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        byte[] noise = Enumerable.Range(0, 512).Select(index => (byte)((index * 31) % 251)).ToArray();
        server.AddMessage(noise);
        server.AddMessage(SyncTestEml.Plain("after_noise", "x"));

        SyncReport report = await harness.SyncAsync();

        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
        Assert.Null(Assert.Single(report.Folders).Error);
        MailMessage noiseRow = (await harness.MessagesOfAsync(Inbox))[0];
        Assert.Equal(noise, await harness.ReadEmlAsync(noiseRow));
    }

    // R5: UIDs are per folder; each folder keeps its own state
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Multiple_folders_keep_their_own_uid_sequences()
    {
        (SyncHarness harness, FakeMailFolder inboxServer) = await StartAsync();
        await using SyncHarness _ = harness;
        FakeMailFolder junkServer = harness.Server.AddFolder("Junk", ServerFolderFlags.Junk);
        inboxServer.AddMessage(SyncTestEml.Plain("inbox_one", "x"));
        inboxServer.AddMessage(SyncTestEml.Plain("inbox_two", "x"));
        junkServer.AddMessage(SyncTestEml.Plain("junk_one", "x"));

        await harness.SyncAsync();

        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync("Junk"));
        Assert.Equal(2u, (await harness.StateOfAsync(Inbox))!.HighestSyncedUid);
        Assert.Equal(1u, (await harness.StateOfAsync("Junk"))!.HighestSyncedUid);
        Assert.Equal(3, harness.EmlFileCount());
    }

    // R5/R6: fetched streams are written to the EML file unchanged (UTF-8 content survives)
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Non_ascii_content_is_stored_byte_identical_and_searchable()
    {
        (SyncHarness harness, FakeMailFolder server) = await StartAsync();
        await using SyncHarness _ = harness;
        byte[] raw = Encoding.UTF8.GetBytes(
            "From: Sender A <sender_a@example.test>\r\nSubject: =?utf-8?B?w5xiZXJzaWNodA==?=\r\n" +
            $"Date: {SyncTestEml.DefaultDateHeader}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" +
            "Grüße aus dem Test, käsekuchenwort\r\n");
        server.AddMessage(raw);

        await harness.SyncAsync();

        MailMessage row = Assert.Single(await harness.MessagesOfAsync(Inbox));
        Assert.Equal("Übersicht", row.Subject);
        Assert.Equal(raw, await harness.ReadEmlAsync(row));
        Assert.Contains("käsekuchenwort", row.TextPreview, StringComparison.Ordinal);
    }
}

using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03a rules R8 (state), R9 (errors), R11 (EML before row) and cases 16, 17, 18.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncErrorTests
{
    private const string Inbox = "INBOX";
    private const string Junk = "Junk";

    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "body");

    private static async Task<(SyncHarness Harness, FakeMailFolder Inbox, FakeMailFolder Junk)> StartAsync()
    {
        SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        FakeMailFolder junk = harness.Server.AddFolder(Junk, ServerFolderFlags.Junk);
        return (harness, inbox, junk);
    }

    /// <summary>Row &lt;=&gt; file: every stored row has an existing EML file.</summary>
    private static async Task AssertEveryRowHasItsFileAsync(SyncHarness harness, string folderName)
    {
        foreach (MailMessage row in await harness.MessagesOfAsync(folderName))
        {
            Assert.NotNull(row.EmlPath);
            Assert.True(harness.Eml.Exists(row.EmlPath), $"EML file of UID {row.Uid} is missing.");
        }
    }

    // Case 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Protocol_error_in_one_folder_is_reported_and_the_next_folder_is_still_synced()
    {
        (SyncHarness harness, FakeMailFolder inbox, FakeMailFolder junk) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        junk.AddMessage(Mail("junk_one"));
        await harness.SyncAsync();
        FolderSyncState stateBefore = (await harness.StateOfAsync(Inbox))!;
        inbox.AddMessage(Mail("inbox_two"));
        junk.AddMessage(Mail("junk_two"));
        harness.Server.OpenFailures[Inbox] = new MailTransportException(MailTransportFailure.Protocol, "Server rejected the command.");
        harness.Clock.Now = SyncHarness.FixedNow.AddHours(1);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult inboxResult = report.Folders.Single(folder => folder.FolderFullName == Inbox);
        FolderSyncResult junkResult = report.Folders.Single(folder => folder.FolderFullName == Junk);
        Assert.NotNull(inboxResult.Error);
        Assert.StartsWith("MailTransportException: ", inboxResult.Error, StringComparison.Ordinal);
        Assert.Contains("Server rejected the command.", inboxResult.Error, StringComparison.Ordinal);
        Assert.Null(junkResult.Error);
        Assert.Equal(1, junkResult.Added);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Junk));
        Assert.Equal(stateBefore, await harness.StateOfAsync(Inbox));
        Assert.Equal(SyncHarness.FixedNow.AddHours(1), (await harness.StateOfAsync(Junk))!.LastSyncedAt);
    }

    // Case 16 (R8), contract of fix.2 F1: one failing message does not block the folder; only it and newer UIDs are retried
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failing_message_in_the_middle_does_not_block_the_folder_and_is_retried_next_time()
    {
        (SyncHarness harness, FakeMailFolder inbox, FakeMailFolder junk) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        await harness.SyncAsync();
        inbox.AddMessage(Mail("inbox_two"));
        inbox.AddMessage(Mail("inbox_three"));
        inbox.AddMessage(Mail("inbox_four"));
        junk.AddMessage(Mail("junk_one"));
        harness.Server.FetchFailures[(Inbox, 3u)] = new MailTransportException(MailTransportFailure.Protocol, "Fetch rejected.");

        SyncReport failed = await harness.SyncAsync();

        Assert.Equal(
            "1 message(s) failed (first: MailTransportException)",
            failed.Folders.Single(folder => folder.FolderFullName == Inbox).Error);
        Assert.Null(failed.Folders.Single(folder => folder.FolderFullName == Junk).Error);
        Assert.Equal(new uint[] { 1, 2, 4 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(2u, (await harness.StateOfAsync(Inbox))!.HighestSyncedUid);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Junk));
        await AssertEveryRowHasItsFileAsync(harness, Inbox);

        harness.Server.FetchFailures.Clear();
        SyncReport resumed = await harness.SyncWithFreshLogAsync();

        Assert.Null(resumed.Folders.Single(folder => folder.FolderFullName == Inbox).Error);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(new[] { (Inbox, 3u) }, harness.Server.Fetches.Where(fetch => fetch.Folder == Inbox).ToArray());
        Assert.Equal(4u, (await harness.StateOfAsync(Inbox))!.HighestSyncedUid);
        Assert.Equal(4 + 1, harness.EmlFileCount());
    }

    // Case 16 (R9): any other exception inside a folder is recorded as "<Type>: <message>"
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Unexpected_exception_in_a_folder_is_recorded_with_type_and_message()
    {
        (SyncHarness harness, FakeMailFolder inbox, FakeMailFolder junk) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        junk.AddMessage(Mail("junk_one"));
        harness.Server.SearchFailures[Inbox] = new InvalidOperationException("boom_message");

        SyncReport report = await harness.SyncAsync();

        Assert.Equal("InvalidOperationException: boom_message", report.Folders.Single(folder => folder.FolderFullName == Inbox).Error);
        Assert.Null(report.Folders.Single(folder => folder.FolderFullName == Junk).Error);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Junk));
    }

    // Case 16 (R9): the transport is disposed when a folder failed
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Transport_is_disposed_exactly_once_after_success_and_after_a_folder_error()
    {
        (SyncHarness harness, FakeMailFolder inbox, _) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        await harness.SyncAsync();
        harness.Server.OpenFailures[Inbox] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");
        await harness.SyncAsync();

        Assert.Equal(2, harness.Server.Transports.Count);
        Assert.All(harness.Server.Transports, transport => Assert.Equal(1, transport.DisposeCalls));
    }

    // Case 17
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_error_aborts_the_sync_and_leaves_rows_and_files_consistent()
    {
        (SyncHarness harness, FakeMailFolder inbox, FakeMailFolder junk) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        inbox.AddMessage(Mail("inbox_two"));
        inbox.AddMessage(Mail("inbox_three"));
        junk.AddMessage(Mail("junk_one"));
        harness.Server.FetchFailures[(Inbox, 3u)] = new MailTransportException(MailTransportFailure.Network, "Connection lost.");

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Network, exception.Failure);
        Assert.Equal(new[] { Inbox }, harness.Server.OpenedFolders.ToArray());
        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
        await AssertEveryRowHasItsFileAsync(harness, Inbox);
        Assert.InRange(harness.EmlFileCount(), 2, 3);
        Assert.Null(await harness.StateOfAsync(Inbox));
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
    }

    // Case 17 / R9: authentication failures reported by the transport abort as well
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(MailTransportFailure.Network)]
    [InlineData(MailTransportFailure.Authentication)]
    public async Task Network_and_authentication_failures_on_open_abort_before_the_next_folder(MailTransportFailure failure)
    {
        (SyncHarness harness, _, FakeMailFolder junk) = await StartAsync();
        await using SyncHarness lifetime = harness;
        junk.AddMessage(Mail("junk_one"));
        harness.Server.OpenFailures[Inbox] = new MailTransportException(failure, "Session failed.");

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(failure, exception.Failure);
        Assert.DoesNotContain(Junk, harness.Server.OpenedFolders);
        Assert.Equal(0, await harness.Storage.CountRowsAsync("messages"));
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
    }

    // Case 17 / R9: connect problems propagate unchanged
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Connect_failures_propagate_unchanged()
    {
        (SyncHarness harness, _, _) = await StartAsync();
        await using SyncHarness lifetime = harness;
        harness.Server.ConnectFailure = new MailTransportException(MailTransportFailure.Network, "Host unreachable.");
        MailTransportException network = await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());
        Assert.Equal(MailTransportFailure.Network, network.Failure);

        var signIn = new AuthenticationFailedException(MailProvider.Google, AuthFailureReason.InteractionRequired, "Sign in again.");
        harness.Server.ConnectFailure = signIn;
        AuthenticationFailedException auth =
            await Assert.ThrowsAsync<AuthenticationFailedException>(() => harness.SyncAsync());

        Assert.Same(signIn, auth);
        Assert.Empty(harness.Server.Transports);
        Assert.Empty(harness.Server.OpenedFolders);
    }

    // Case 17 / R9: an authentication problem surfacing during the session propagates too
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task AuthenticationFailedException_during_a_folder_aborts_the_sync()
    {
        (SyncHarness harness, FakeMailFolder inbox, _) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        harness.Server.OpenFailures[Inbox] =
            new AuthenticationFailedException(MailProvider.Google, AuthFailureReason.InteractionRequired, "Sign in again.");

        await Assert.ThrowsAsync<AuthenticationFailedException>(() => harness.SyncAsync());

        Assert.DoesNotContain(Junk, harness.Server.OpenedFolders);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
    }

    // Case 18
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Cancellation_after_two_of_four_messages_then_a_new_run_completes_without_duplicates()
    {
        (SyncHarness harness, FakeMailFolder inbox, _) = await StartAsync();
        await using SyncHarness lifetime = harness;
        for (int index = 1; index <= 4; index++)
        {
            inbox.AddMessage(Mail($"inbox_{index}"));
        }

        using var cancellation = new CancellationTokenSource();
        harness.Server.OnFetch = (_, uid) =>
        {
            if (uid == 3)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SyncAsync(cancellation.Token));

        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
        await AssertEveryRowHasItsFileAsync(harness, Inbox);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);

        harness.Server.OnFetch = null;
        SyncReport resumed = await harness.SyncWithFreshLogAsync();

        Assert.Null(resumed.Folders.Single(folder => folder.FolderFullName == Inbox).Error);
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(4, await harness.Storage.CountRowsAsync("messages"));
        Assert.Equal(4, harness.EmlFileCount());
        Assert.DoesNotContain((Inbox, 1u), harness.Server.Fetches);
        Assert.DoesNotContain((Inbox, 2u), harness.Server.Fetches);
        Assert.Equal(4u, (await harness.StateOfAsync(Inbox))!.HighestSyncedUid);
        await AssertEveryRowHasItsFileAsync(harness, Inbox);
    }

    // Case 18 (R9): a token cancelled before the start never touches the server state
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Already_cancelled_token_throws_OperationCanceledException_and_stores_nothing()
    {
        (SyncHarness harness, FakeMailFolder inbox, _) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SyncAsync(cancellation.Token));

        Assert.Equal(0, await harness.Storage.CountRowsAsync("messages"));
        Assert.Equal(0, harness.EmlFileCount());
    }

    // Case 18 (R9): OperationCanceledException raised by the transport inside a folder is not swallowed as a folder error
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task OperationCanceledException_from_the_transport_propagates()
    {
        (SyncHarness harness, FakeMailFolder inbox, _) = await StartAsync();
        await using SyncHarness lifetime = harness;
        inbox.AddMessage(Mail("inbox_one"));
        harness.Server.SearchFailures[Inbox] = new OperationCanceledException("Cancelled by test.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.SyncAsync());

        Assert.DoesNotContain(Junk, harness.Server.OpenedFolders);
    }
}

using MailWarden.Core.Accounts;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03b B4: reconnect within a pass (cases 9-13). The fake drops the connection after a Network failure.</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncReconnectTests
{
    private const string Inbox = "INBOX";
    private const string Sent = "Sent";
    private const string Junk = "Junk";

    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "body");

    private static async Task<SyncHarness> StartWithThreeFoldersAsync()
    {
        SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox).AddMessage(Mail("inbox_one"));
        harness.Server.AddFolder(Sent, ServerFolderFlags.Sent).AddMessage(Mail("sent_one"));
        harness.Server.AddFolder(Junk, ServerFolderFlags.Junk).AddMessage(Mail("junk_one"));
        return harness;
    }

    private static MailTransportException NetworkLoss() =>
        new(MailTransportFailure.Network, "Connection lost.");

    // Case 9
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_failure_in_the_second_folder_reconnects_and_syncs_the_third_on_the_new_transport()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        await harness.SyncAsync();
        FolderSyncState stateBefore = (await harness.StateOfAsync(Sent))!;
        harness.Server.Folder(Sent).AddMessage(Mail("sent_two"));
        harness.Server.Folder(Junk).AddMessage(Mail("junk_two"));
        harness.Server.OpenFailures[Sent] = NetworkLoss();
        harness.Server.ClearLog();
        harness.Server.Transports.Clear();
        harness.Server.ConnectCalls = 0;
        harness.Clock.Now = SyncHarness.FixedNow.AddHours(1);

        SyncReport report = await harness.SyncAsync();

        FolderSyncResult sentResult = report.Folders.Single(folder => folder.FolderFullName == Sent);
        Assert.StartsWith("MailTransportException", sentResult.Error, StringComparison.Ordinal);
        Assert.Null(report.Folders.Single(folder => folder.FolderFullName == Inbox).Error);
        Assert.Null(report.Folders.Single(folder => folder.FolderFullName == Junk).Error);
        Assert.Equal(stateBefore, await harness.StateOfAsync(Sent));
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Sent));
        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Junk));
        Assert.Equal(2, harness.Server.ConnectCalls);
        Assert.Equal(2, harness.Server.Transports.Count);
        FakeMailTransport oldTransport = harness.Server.Transports[0];
        FakeMailTransport newTransport = harness.Server.Transports[1];
        Assert.Equal(1, oldTransport.DisposeCalls);
        Assert.Equal(1, newTransport.DisposeCalls);
        Assert.Equal(new[] { Inbox, Sent }, oldTransport.OpenedFolders.ToArray());
        Assert.Equal(new[] { Junk }, newTransport.OpenedFolders.ToArray());
    }

    // Case 9 (supporting): a Network failure while fetching a batch behaves the same
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_failure_during_a_batch_fails_the_folder_and_reconnects()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        harness.Server.FetchFailures[(Sent, 1u)] = NetworkLoss();

        SyncReport report = await harness.SyncAsync();

        string? sentError = report.Folders.Single(folder => folder.FolderFullName == Sent).Error;
        Assert.StartsWith("MailTransportException", sentError, StringComparison.Ordinal);
        Assert.Null(report.Folders.Single(folder => folder.FolderFullName == Junk).Error);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Junk));
        Assert.Equal(2, harness.Server.ConnectCalls);
        Assert.All(harness.Server.Transports, transport => Assert.Equal(1, transport.DisposeCalls));
    }

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Too_many_lost_connections_abort_the_pass_with_a_network_error_and_keep_earlier_results()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox).AddMessage(Mail("inbox_one"));
        (string Name, ServerFolderFlags Flags)[] failing =
        [
            (Sent, ServerFolderFlags.Sent),
            (Junk, ServerFolderFlags.Junk),
            ("Drafts", ServerFolderFlags.Drafts),
            ("Archive", ServerFolderFlags.None),
        ];
        foreach ((string name, ServerFolderFlags flags) in failing)
        {
            harness.Server.AddFolder(name, flags);
            harness.Server.OpenFailures[name] = NetworkLoss();
        }

        harness.UseOptions(new SyncOptions { MaxReconnectsPerPass = 2 });

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Network, exception.Failure);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(3, harness.Server.Transports.Count);
        Assert.All(harness.Server.Transports, transport => Assert.Equal(1, transport.DisposeCalls));
        Assert.DoesNotContain("Archive", harness.Server.OpenedFolders);
    }

    // Case 10 (supporting): MaxReconnectsPerPass 0 means the first lost connection already aborts
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Zero_allowed_reconnects_abort_on_the_first_lost_connection()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        harness.Server.OpenFailures[Sent] = NetworkLoss();
        harness.UseOptions(new SyncOptions { MaxReconnectsPerPass = 0 });

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Network, exception.Failure);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
        Assert.Equal(1, harness.Server.ConnectCalls);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
        Assert.DoesNotContain(Junk, harness.Server.OpenedFolders);
    }

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Protocol_failure_with_a_connected_transport_does_not_reconnect()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        harness.Server.OpenFailures[Sent] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");

        SyncReport report = await harness.SyncAsync();

        Assert.NotNull(report.Folders.Single(folder => folder.FolderFullName == Sent).Error);
        Assert.Null(report.Folders.Single(folder => folder.FolderFullName == Junk).Error);
        Assert.Equal(1, harness.Server.ConnectCalls);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Junk));
    }

    // Case 11 (B4): the decision to reconnect follows IsConnected, not the failure type
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Protocol_failure_after_which_the_session_is_gone_still_reconnects()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        // MailKit disconnects on a protocol error: the failure is Protocol, but the session is gone.
        harness.Server.AnyFailureDropsConnection = true;
        harness.Server.OpenFailures[Sent] = new MailTransportException(MailTransportFailure.Protocol, "Protocol error.");

        SyncReport report = await harness.SyncAsync();

        Assert.NotNull(report.Folders.Single(folder => folder.FolderFullName == Sent).Error);
        Assert.Null(report.Folders.Single(folder => folder.FolderFullName == Junk).Error);
        Assert.Equal(2, harness.Server.ConnectCalls);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Junk));
        Assert.All(harness.Server.Transports, transport => Assert.Equal(1, transport.DisposeCalls));
    }

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Authentication_failure_inside_a_folder_aborts_the_pass()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        harness.Server.FetchFailures[(Sent, 1u)] =
            new MailTransportException(MailTransportFailure.Authentication, "Sign in again.");

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Authentication, exception.Failure);
        Assert.DoesNotContain(Junk, harness.Server.OpenedFolders);
        Assert.Equal(1, harness.Server.ConnectCalls);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
    }

    // Case 12 (B4): failures of the folder list abort the pass, even Network ones
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_failure_while_reading_the_folder_list_aborts_without_reconnect()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        harness.Server.ListFailure = NetworkLoss();

        MailTransportException exception =
            await Assert.ThrowsAsync<MailTransportException>(() => harness.SyncAsync());

        Assert.Equal(MailTransportFailure.Network, exception.Failure);
        Assert.Equal(1, harness.Server.ConnectCalls);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
        Assert.Empty(harness.Server.OpenedFolders);
    }

    // Case 13
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Factory_failure_on_reconnect_aborts_the_pass_with_that_exception()
    {
        await using SyncHarness harness = await StartWithThreeFoldersAsync();
        harness.Server.OpenFailures[Sent] = NetworkLoss();
        var reconnectFailure = new InvalidOperationException("reconnect_boom");
        harness.Server.ConnectFailuresByAttempt[2] = reconnectFailure;

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SyncAsync());

        Assert.Same(reconnectFailure, exception);
        Assert.Equal(2, harness.Server.ConnectCalls);
        Assert.Equal(1, Assert.Single(harness.Server.Transports).DisposeCalls);
        Assert.Equal(new uint[] { 1 }, await harness.StoredUidsAsync(Inbox));
        Assert.DoesNotContain(Junk, harness.Server.OpenedFolders);
    }
}

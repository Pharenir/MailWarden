using MailWarden.Core.Accounts;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Commands;

namespace MailWarden.Infrastructure.Tests.Commands;

/// <summary>
/// AP-03c cases 14–21: retry, backoff, failure classes (E5), error text, ordering and one session (E1), and the rule
/// that the local store changes only after the server succeeded (E4).
/// </summary>
[Trait("Category", "feature:sync")]
public sealed class CommandFailureTests
{
    private const string SecretMarker = "SECRET_MARKER_7f3a91";

    private static MailTransportException Protocol(string message = "Injected protocol failure.") =>
        new(MailTransportFailure.Protocol, message);

    private static Func<CommandCall, Exception?> FailOn(string kind, Func<Exception> failure) =>
        call => call.Kind == kind ? failure() : null;

    private static async Task<MailCommand> EnqueueSeenAsync(CommandHarness harness, SeededFolder folder, uint uid)
    {
        MailCommand command = await harness.Queue.EnqueueSetFlagsAsync(folder.Folder.Id, [uid], MessageFlags.Seen, MessageFlags.None);
        harness.Advance(TimeSpan.FromSeconds(1));
        return command;
    }

    // Case 14
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Protocol_or_other_failure_records_the_attempt_and_the_next_command_still_runs(bool protocolFailure)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        MailCommand first = await EnqueueSeenAsync(harness, inbox, 1);
        await EnqueueSeenAsync(harness, inbox, 2);
        DateTimeOffset now = harness.Clock.Now;
        harness.Server.CommandFailure = call =>
            call.Kind == CommandCall.AddFlags && call.Uids.Contains(1u)
                ? (protocolFailure ? Protocol() : (Exception)new InvalidOperationException("Injected failure."))
                : null;

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 1, 0), report);
        MailCommand retry = await harness.SingleCommandAsync();
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(MailCommandStatus.Pending, retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.Equal(now + TimeSpan.FromMinutes(1), retry.NextAttemptAt);
        Assert.False(string.IsNullOrWhiteSpace(retry.LastError));
        Assert.Equal(MessageFlags.None, inbox.Server.Messages[1].Flags);
        Assert.Equal(MessageFlags.Seen, inbox.Server.Messages[2].Flags);
        Assert.Equal(MessageFlags.None, (await harness.Storage.Messages.GetUidFlagsAsync(inbox.Folder.Id))[1]);
        Assert.Equal(MessageFlags.Seen, (await harness.Storage.Messages.GetUidFlagsAsync(inbox.Folder.Id))[2]);
        Assert.Equal(1, harness.Server.CommandConnectCalls);
        Assert.All(harness.Server.CommandTransports, transport => Assert.True(transport.Disposed));
    }

    // Cases 15 and 16
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Backoff_doubles_from_one_minute_and_is_capped_at_sixty_minutes()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync(maxAttempts: 20);
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        harness.Server.CommandFailure = FailOn(CommandCall.AddFlags, () => Protocol());
        int[] expectedMinutes = [1, 2, 4, 8, 16, 32, 60, 60, 60];

        for (int attempt = 1; attempt <= expectedMinutes.Length; attempt++)
        {
            DateTimeOffset runAt = harness.Clock.Now;
            CommandRunReport report = await harness.ExecuteAsync();

            Assert.Equal(new CommandRunReport(0, 1, 0), report);
            MailCommand command = await harness.SingleCommandAsync();
            Assert.Equal(attempt, command.Attempts);
            Assert.Equal(MailCommandStatus.Pending, command.Status);
            Assert.Equal(runAt + TimeSpan.FromMinutes(expectedMinutes[attempt - 1]), command.NextAttemptAt);

            int connectsBefore = harness.Server.CommandConnectCalls;
            harness.Advance(TimeSpan.FromMinutes(expectedMinutes[attempt - 1]) - TimeSpan.FromSeconds(1));
            Assert.Equal(new CommandRunReport(0, 0, 0), await harness.ExecuteAsync());
            Assert.Equal(connectsBefore, harness.Server.CommandConnectCalls);
            harness.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_attempt_that_reaches_max_attempts_fails_the_command_for_good()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync(maxAttempts: 5);
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        harness.Server.CommandFailure = FailOn(CommandCall.AddFlags, () => Protocol());

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(new CommandRunReport(0, 1, 0), await harness.ExecuteAsync());
            Assert.Equal(MailCommandStatus.Pending, (await harness.SingleCommandAsync()).Status);
            harness.Advance(TimeSpan.FromMinutes(60));
        }

        CommandRunReport last = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 0, 1), last);
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(MailCommandStatus.Failed, command.Status);
        Assert.Equal(5, command.Attempts);
        Assert.Empty(await harness.Queue.ListDueAsync(harness.Account.Id, harness.Advance(TimeSpan.FromDays(7))));
        int connects = harness.Server.CommandConnectCalls;
        Assert.Equal(new CommandRunReport(0, 0, 0), await harness.ExecuteAsync());
        Assert.Equal(connects, harness.Server.CommandConnectCalls);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task With_max_attempts_one_the_first_failure_is_final()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync(maxAttempts: 1);
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        harness.Server.CommandFailure = FailOn(CommandCall.AddFlags, () => Protocol());

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 0, 1), report);
        Assert.Equal(MailCommandStatus.Failed, (await harness.SingleCommandAsync()).Status);
    }

    // Case 17
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Network_failure_records_the_attempt_ends_the_run_and_disposes_the_transport()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        MailCommand first = await EnqueueSeenAsync(harness, inbox, 1);
        await EnqueueSeenAsync(harness, inbox, 2);
        await EnqueueSeenAsync(harness, inbox, 3);
        harness.Server.CommandFailure = FailOn(
            CommandCall.AddFlags,
            () => new MailTransportException(MailTransportFailure.Network, "Injected network failure."));

        MailTransportException exception = await Assert.ThrowsAsync<MailTransportException>(() => harness.ExecuteAsync());

        Assert.Equal(MailTransportFailure.Network, exception.Failure);
        IReadOnlyList<MailCommand> commands = await harness.ListAsync();
        Assert.Equal(3, commands.Count);
        Assert.Equal(first.Id, commands[0].Id);
        Assert.Equal(1, commands[0].Attempts);
        Assert.Equal(MailCommandStatus.Pending, commands[0].Status);
        Assert.NotNull(commands[0].NextAttemptAt);
        Assert.All(commands.Skip(1), command =>
        {
            Assert.Equal(0, command.Attempts);
            Assert.Equal(MailCommandStatus.Pending, command.Status);
            Assert.Null(command.NextAttemptAt);
            Assert.Null(command.LastError);
        });
        Assert.Single(harness.Server.CommandCalls, call => call.Kind == CommandCall.AddFlags);
        Assert.Equal(3, inbox.Server.Messages.Count);
        Assert.All(inbox.Server.Messages.Values, message => Assert.Equal(MessageFlags.None, message.Flags));
        FakeCommandTransport transport = Assert.Single(harness.Server.CommandTransports);
        Assert.True(transport.Disposed);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Connect_failure_with_network_error_propagates_and_leaves_the_commands_unchanged()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        harness.Server.CommandConnectFailure = new MailTransportException(MailTransportFailure.Network, "Injected connect failure.");

        MailTransportException exception = await Assert.ThrowsAsync<MailTransportException>(() => harness.ExecuteAsync());

        Assert.Equal(MailTransportFailure.Network, exception.Failure);
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(0, command.Attempts);
        Assert.Equal(MailCommandStatus.Pending, command.Status);
        Assert.Empty(harness.Server.CommandCalls);
    }

    // Case 18
    private static Exception AuthOrCancel(int kind) => kind switch
    {
        0 => new MailTransportException(MailTransportFailure.Authentication, "Injected login failure."),
        1 => new AuthenticationFailedException(
            MailProvider.Google, AuthFailureReason.InteractionRequired, "Injected sign-in needed."),
        _ => new OperationCanceledException(),
    };

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Authentication_failure_and_cancellation_propagate_without_counting_an_attempt(int kind)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        MailCommand first = await EnqueueSeenAsync(harness, inbox, 1);
        await EnqueueSeenAsync(harness, inbox, 2);
        Exception expected = AuthOrCancel(kind);
        harness.Server.CommandFailure = FailOn(CommandCall.AddFlags, () => expected);

        Exception? thrown = await Record.ExceptionAsync(() => harness.ExecuteAsync());

        Assert.NotNull(thrown);
        Assert.IsType(expected.GetType(), thrown);
        IReadOnlyList<MailCommand> commands = await harness.ListAsync();
        Assert.Equal(2, commands.Count);
        Assert.Equal(first.Id, commands[0].Id);
        Assert.All(commands, command =>
        {
            Assert.Equal(0, command.Attempts);
            Assert.Equal(MailCommandStatus.Pending, command.Status);
            Assert.Null(command.NextAttemptAt);
            Assert.Null(command.LastError);
        });
        Assert.Single(harness.Server.CommandCalls, call => call.Kind == CommandCall.AddFlags);
        Assert.All(inbox.Server.Messages.Values, message => Assert.Equal(MessageFlags.None, message.Flags));
        Assert.All(harness.Server.CommandTransports, transport => Assert.True(transport.Disposed));
    }

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Authentication_failure_while_connecting_propagates_and_leaves_the_commands_unchanged(int kind)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        Exception expected = AuthOrCancel(kind);
        harness.Server.CommandConnectFailure = expected;

        Exception? thrown = await Record.ExceptionAsync(() => harness.ExecuteAsync());

        Assert.NotNull(thrown);
        Assert.IsType(expected.GetType(), thrown);
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(0, command.Attempts);
        Assert.Null(command.LastError);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_cancelled_token_propagates_and_changes_nothing()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ExecuteAsync(cancellation.Token));

        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(0, command.Attempts);
        Assert.Null(command.LastError);
        Assert.Empty(harness.Server.WriteCalls);
        Assert.Equal(MessageFlags.None, inbox.Server.Messages[1].Flags);
    }

    // Case 19
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Storage_failure_in_the_local_delete_after_a_server_move_still_completes_the_command()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        SeededFolder archive = await harness.SeedFolderAsync("Archive", FolderRole.Archive);
        await harness.Queue.EnqueueMoveAsync(inbox.Folder.Id, [1], archive.Folder.Id);
        harness.Messages.FailDeleteByUids = true;

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Empty(await harness.ListAsync());
        Assert.Equal(new uint[] { 2 }, harness.Server.ServerUids("INBOX"));
        Assert.Single(harness.Server.ServerUids("Archive"));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Storage_failure_in_the_local_flag_update_after_server_success_still_completes_the_command()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        harness.Messages.FailUpdateFlags = true;

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Empty(await harness.ListAsync());
        Assert.Equal(MessageFlags.Seen, inbox.Server.Messages[1].Flags);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_failing_eml_delete_is_skipped_and_the_rows_are_still_removed()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder junk = await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 2);
        harness.FaultyEml.FailDeleteWhen = path => path.EndsWith("/1.eml", StringComparison.Ordinal);
        await harness.Queue.EnqueueDeletePermanentlyAsync(junk.Folder.Id, [1, 2]);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), report);
        Assert.Empty(await harness.LocalUidsAsync(junk.Folder));
        Assert.Empty(harness.Server.ServerUids("Junk"));
        Assert.Empty(await harness.ListAsync());
    }

    // Case 20
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Commands_run_oldest_first_over_one_session()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder junk = await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 1);
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        SeededFolder archive = await harness.SeedFolderAsync("Archive", FolderRole.Archive, messageCount: 1);
        await EnqueueSeenAsync(harness, junk, 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        await EnqueueSeenAsync(harness, archive, 1);

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(3, 0, 0), report);
        Assert.Equal(1, harness.Server.CommandConnectCalls);
        Assert.Equal(0, harness.Server.ConnectCalls);
        Assert.Equal(
            new[] { "Junk", "INBOX", "Archive" },
            harness.Server.CommandCalls.Where(call => call.Kind == CommandCall.Select).Select(call => call.Folder).ToArray());
        FakeCommandTransport transport = Assert.Single(harness.Server.CommandTransports);
        Assert.Equal(1, transport.DisposeCalls);
    }

    // Case 21
    private static Exception MarkerException(int kind) => kind switch
    {
        0 => Protocol(SecretMarker),
        1 => new InvalidOperationException(SecretMarker),
        2 => new ArgumentException(SecretMarker),
        3 => new MailTransportException(MailTransportFailure.Protocol, "Injected.", new InvalidOperationException(SecretMarker)),
        _ => new MailTransportException(MailTransportFailure.Network, SecretMarker),
    };

    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Last_error_never_contains_the_exception_message(int kind)
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        Exception failure = MarkerException(kind);
        harness.Server.CommandFailure = FailOn(CommandCall.AddFlags, () => failure);

        Exception? thrown = await Record.ExceptionAsync(() => harness.ExecuteAsync());

        Assert.Equal(kind == 4, thrown is not null);
        MailCommand command = await harness.SingleCommandAsync();
        Assert.Equal(1, command.Attempts);
        Assert.False(string.IsNullOrWhiteSpace(command.LastError));
        Assert.DoesNotContain(SecretMarker, command.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Last_error_of_a_permanent_failure_is_a_fixed_text_without_server_data()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        inbox.Server.UidValidity = 4242;

        await harness.ExecuteAsync();

        MailCommand command = await harness.SingleCommandAsync();
        Assert.DoesNotContain("4242", command.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(inbox.Folder.FullName, command.LastError, StringComparison.Ordinal);
    }

    // Local effect only after server success (E4) and safety of the delete path under failures
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_flag_call_leaves_the_local_flags_unchanged()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 1);
        await EnqueueSeenAsync(harness, inbox, 1);
        harness.Server.CommandFailure = FailOn(CommandCall.AddFlags, () => Protocol());

        await harness.ExecuteAsync();

        Assert.Equal(MessageFlags.None, (await harness.Storage.Messages.GetUidFlagsAsync(inbox.Folder.Id))[1]);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_move_leaves_rows_and_eml_files_and_retries_later()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        SeededFolder archive = await harness.SeedFolderAsync("Archive", FolderRole.Archive);
        string[] paths = await harness.EmlPathsAsync(inbox.Folder, 1, 2);
        await harness.Queue.EnqueueMoveAsync(inbox.Folder.Id, [1], archive.Folder.Id);
        harness.Server.CommandFailure = FailOn(CommandCall.Move, () => Protocol());

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 1, 0), report);
        Assert.Equal(new uint[] { 1, 2 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.All(paths, path => Assert.True(harness.EmlExists(path)));
        Assert.Equal(new uint[] { 1, 2 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(1, (await harness.SingleCommandAsync()).Attempts);
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failed_expunge_leaves_rows_and_eml_files()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder junk = await harness.SeedFolderAsync("Junk", FolderRole.Junk, messageCount: 2);
        string[] paths = await harness.EmlPathsAsync(junk.Folder, 1, 2);
        await harness.Queue.EnqueueDeletePermanentlyAsync(junk.Folder.Id, [1]);
        harness.Server.CommandFailure = FailOn(CommandCall.Expunge, () => Protocol());

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 1, 0), report);
        Assert.Equal(new uint[] { 1, 2 }, await harness.LocalUidsAsync(junk.Folder));
        Assert.All(paths, path => Assert.True(harness.EmlExists(path)));
        Assert.Equal(new uint[] { 1, 2 }, harness.Server.ServerUids("Junk"));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_outside_trash_never_expunges_when_the_move_fails()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 2);
        await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, [1, 2]);
        harness.Server.CommandFailure = FailOn(CommandCall.Move, () => Protocol());

        CommandRunReport report = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 1, 0), report);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(new uint[] { 1, 2 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(new uint[] { 1, 2 }, await harness.LocalUidsAsync(inbox.Folder));
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Permanent_delete_outside_trash_never_expunges_when_selecting_trash_fails_and_the_retry_deletes_nothing_more()
    {
        await using CommandHarness harness = await CommandHarness.CreateAsync();
        SeededFolder inbox = await harness.SeedFolderAsync("INBOX", FolderRole.Inbox, messageCount: 3);
        SeededFolder trash = await harness.SeedFolderAsync("Trash", FolderRole.Trash);
        uint preExisting = trash.Server.AddMessage(SyncTestEml.Plain("old_in_trash", "body"));
        await harness.Queue.EnqueueDeletePermanentlyAsync(inbox.Folder.Id, [1, 2]);
        harness.Server.CommandFailure = call =>
            call is { Kind: CommandCall.Select, Folder: "Trash" } ? Protocol() : null;

        CommandRunReport first = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(0, 1, 0), first);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(3, harness.Server.ServerUids("Trash").Length);
        Assert.Equal(new uint[] { 1, 2, 3 }, await harness.LocalUidsAsync(inbox.Folder));

        harness.Server.CommandFailure = null;
        harness.Advance(TimeSpan.FromMinutes(5));
        CommandRunReport second = await harness.ExecuteAsync();

        Assert.Equal(new CommandRunReport(1, 0, 0), second);
        Assert.DoesNotContain(harness.Server.CommandCalls, call => call.Kind == CommandCall.Expunge);
        Assert.Equal(3, harness.Server.ServerUids("Trash").Length);
        Assert.Contains(preExisting, harness.Server.ServerUids("Trash"));
        Assert.Equal(new uint[] { 3 }, harness.Server.ServerUids("INBOX"));
        Assert.Equal(new uint[] { 3 }, await harness.LocalUidsAsync(inbox.Folder));
        Assert.Empty(await harness.ListAsync());
    }
}

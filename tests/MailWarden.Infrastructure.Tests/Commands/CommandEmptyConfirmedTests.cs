using MailWarden.Core.Commands;

namespace MailWarden.Infrastructure.Tests.Commands;

/// <summary>AP-04b fix.4 (W2): the queue empties a folder only for UIDs the caller confirmed, check and insert in one step.</summary>
[Trait("Category", "feature:sync")]
[Trait("Category", "feature:storage")]
public sealed class CommandEmptyConfirmedTests
{
    // W-a at queue level: another UID with the same count and highest UID.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_local_uid_that_was_not_confirmed_throws_EmptyFolderChangedException_and_stores_nothing()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.SeedAsync(fixture.Junk, 15, 20);

        await Assert.ThrowsAsync<EmptyFolderChangedException>(() =>
            fixture.Queue.EnqueueEmptyFolderAsync(fixture.Junk.Id, fixture.Junk.UidValidity, new uint[] { 10, 20 }));

        Assert.Equal(0L, await fixture.CommandRowCountAsync());
    }

    // W-b at queue level.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_different_uid_validity_throws_EmptyFolderChangedException_and_stores_nothing()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.SeedAsync(fixture.Junk, 1, 2);

        await Assert.ThrowsAsync<EmptyFolderChangedException>(() =>
            fixture.Queue.EnqueueEmptyFolderAsync(fixture.Junk.Id, fixture.Junk.UidValidity + 1, new uint[] { 1, 2 }));

        Assert.Equal(0L, await fixture.CommandRowCountAsync());
    }

    // W-c at queue level: positive control, a subset passes and stores exactly the local UIDs.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_subset_of_the_confirmed_uids_stores_exactly_the_local_uids()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();
        await fixture.SeedAsync(fixture.Junk, 2, 4);

        MailCommand? command = await fixture.Queue.EnqueueEmptyFolderAsync(fixture.Junk.Id, fixture.Junk.UidValidity, new uint[] { 1, 2, 3, 4 });

        Assert.NotNull(command);
        Assert.Equal(new uint[] { 2, 4 }, command.Uids);
        Assert.Equal(1L, await fixture.CommandRowCountAsync());
    }

    // W-d at queue level: nothing local means null, not an exception.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Nothing_local_returns_null_and_does_not_throw()
    {
        await using CommandQueueFixture fixture = await CommandQueueFixture.CreateAsync();

        MailCommand? command = await fixture.Queue.EnqueueEmptyFolderAsync(fixture.Junk.Id, fixture.Junk.UidValidity, new uint[] { 1, 2 });

        Assert.Null(command);
        Assert.Equal(0L, await fixture.CommandRowCountAsync());
    }
}

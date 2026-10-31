using MailWarden.Core.Accounts;
using MailWarden.Core.Model;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Infrastructure.Tests.Sync;

/// <summary>AP-03b B3: progress reports of a running pass (cases 7 and 8).</summary>
[Trait("Category", "feature:sync")]
public sealed class SyncProgressTests
{
    private const string Inbox = "INBOX";
    private const string Sent = "Sent";

    private static byte[] Mail(string subject) => SyncTestEml.Plain(subject, "body");

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Reports_follow_folder_start_candidates_batches_and_folder_end_in_order()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        FakeMailFolder sent = harness.Server.AddFolder(Sent, ServerFolderFlags.Sent);
        for (int index = 1; index <= 60; index++)
        {
            inbox.AddMessage(Mail($"inbox_{index}"));
        }

        for (int index = 1; index <= 3; index++)
        {
            sent.AddMessage(Mail($"sent_{index}"));
        }

        harness.UseOptions(new SyncOptions { FetchBatchSize = 50 });
        var progress = new RecordingProgress();

        await harness.Engine.SyncAccountAsync(harness.Account.Id, progress);

        Assert.Equal(
            new (int FolderNumber, int Processed, int Candidates)[]
            {
                (1, 0, 0), (1, 0, 60), (1, 50, 60), (1, 60, 60), (1, 60, 60),
                (2, 0, 0), (2, 0, 3), (2, 3, 3), (2, 3, 3),
            },
            progress.Reports.Select(report => (report.FolderNumber, report.Processed, report.Candidates)).ToArray());
        Assert.All(progress.Reports, report => Assert.Equal(2, report.FolderCount));
        Assert.All(progress.Reports, report => Assert.Equal(harness.Account.Id, report.AccountId));
        long inboxId = (await harness.GetFolderAsync(Inbox)).Id;
        long sentId = (await harness.GetFolderAsync(Sent)).Id;
        Assert.All(progress.Reports.Where(report => report.FolderNumber == 1), report =>
        {
            Assert.Equal(inboxId, report.FolderId);
            Assert.Equal(FolderRole.Inbox, report.Role);
        });
        Assert.All(progress.Reports.Where(report => report.FolderNumber == 2), report =>
        {
            Assert.Equal(sentId, report.FolderId);
            Assert.Equal(FolderRole.Sent, report.Role);
        });
    }

    // Case 7: a folder that fails still gets its start and end report
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Failing_folder_reports_start_and_end_and_the_next_folder_reports_too()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        FakeMailFolder sent = harness.Server.AddFolder(Sent, ServerFolderFlags.Sent);
        sent.AddMessage(Mail("sent_one"));
        harness.Server.OpenFailures[Inbox] = new MailTransportException(MailTransportFailure.Protocol, "Rejected.");
        var progress = new RecordingProgress();

        await harness.Engine.SyncAccountAsync(harness.Account.Id, progress);

        SyncProgress[] first = progress.Reports.Where(report => report.FolderNumber == 1).ToArray();
        Assert.True(first.Length >= 2, "A failing folder reports at least its start and its end.");
        Assert.Equal(0, first[0].Processed);
        Assert.Equal(0, first[0].Candidates);
        SyncProgress[] second = progress.Reports.Where(report => report.FolderNumber == 2).ToArray();
        Assert.Equal(0, second[0].Processed);
        Assert.Equal(1, second[^1].Processed);
        Assert.Equal(1, second[^1].Candidates);
    }

    // Case 7: no report carries a name, subject or address
    [Fact]
    public void Progress_record_has_no_string_member()
    {
        Type[] allowed = [typeof(Guid), typeof(long), typeof(FolderRole), typeof(int)];

        Assert.All(typeof(SyncProgress).GetProperties(), property => Assert.Contains(property.PropertyType, allowed));
    }

    // Case 8
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Null_progress_works_like_the_overload_without_progress()
    {
        await using SyncHarness harness = await SyncHarness.CreateAsync();
        FakeMailFolder inbox = harness.Server.AddFolder(Inbox, ServerFolderFlags.Inbox);
        inbox.AddMessage(Mail("inbox_one"));
        inbox.AddMessage(Mail("inbox_two"));

        SyncReport report = await harness.Engine.SyncAccountAsync(harness.Account.Id, (IProgress<SyncProgress>?)null);

        Assert.Null(Assert.Single(report.Folders).Error);
        Assert.Equal(new uint[] { 1, 2 }, await harness.StoredUidsAsync(Inbox));
    }
}

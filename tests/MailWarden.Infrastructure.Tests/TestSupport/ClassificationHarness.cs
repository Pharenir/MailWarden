using System.Text;
using Microsoft.Data.Sqlite;
using MailWarden.Core.Classification;
using MailWarden.Core.Commands;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Classification;
using MailWarden.Infrastructure.Commands;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// Real in-memory SQLite stores, real file EML store, real queue, the fake provisioner and a fixed clock, wired to a
/// <see cref="ClassificationPass"/>. One account with Inbox (validity 1000) and optionally Junk (2000), Screener (3000)
/// and Quarantine (4000), and one contact (<see cref="ClassificationTestEml.ContactAddress"/>). All data is artificial.
/// </summary>
internal sealed class ClassificationHarness : IAsyncDisposable
{
    public const uint InboxUidValidity = 1000;

    public const uint JunkUidValidity = 2000;

    public const uint SentUidValidity = 5000;

    public static readonly ClassificationPolicy RecordOnly = new(ClassificationMode.RecordOnly, 0);

    public static readonly ClassificationPolicy Act = new(ClassificationMode.Act, 0);

    private readonly TempDirectory temp;

    private ClassificationHarness(StorageFixture storage, TempDirectory temp, Account account)
    {
        Storage = storage;
        this.temp = temp;
        Account = account;
        Eml = new FileEmlStore(new DataPaths(temp.RootPath));
        Clock = new FixedTimeProvider(SyncHarness.FixedNow);
        Queue = new SqliteMailCommandQueue(storage.Database, Clock);
        Contacts = new SqliteContactStore(storage.Database);
        SenderLists = new SqliteSenderListStore(storage.Database);
        HarvestMarkers = new SqliteContactHarvestMarkerStore(storage.Database);
        Log = new SqliteClassificationLog(storage.Database);
        SyncStates = new SqliteFolderSyncStateStore(storage.Database);
        Provisioner = new FakeFolderProvisioner(storage.Folders);
    }

    public StorageFixture Storage { get; }

    public Account Account { get; }

    public FileEmlStore Eml { get; }

    public FixedTimeProvider Clock { get; }

    public SqliteMailCommandQueue Queue { get; }

    public SqliteContactStore Contacts { get; }

    public SqliteSenderListStore SenderLists { get; }

    public SqliteContactHarvestMarkerStore HarvestMarkers { get; }

    public SqliteClassificationLog Log { get; }

    public SqliteFolderSyncStateStore SyncStates { get; }

    public FakeFolderProvisioner Provisioner { get; }

    public MailFolder Inbox { get; private set; } = null!;

    public MailFolder? Junk { get; private set; }

    public MailFolder? Screener { get; private set; }

    public MailFolder? Quarantine { get; private set; }

    /// <summary>Sent folder (validity <see cref="SentUidValidity"/>), only with <c>withSent</c>.</summary>
    public MailFolder? Sent { get; private set; }

    /// <summary>Creates account, folders and (optionally) the one contact.</summary>
    public static async Task<ClassificationHarness> CreateAsync(
        bool withJunk = true,
        bool withScreener = true,
        bool withQuarantine = true,
        bool withContact = true,
        bool withSyncState = true,
        bool withSent = false)
    {
        StorageFixture storage = await StorageFixture.CreateAsync();
        var temp = new TempDirectory();
        Account account = TestData.NewAccount("Account_A");
        await storage.Accounts.UpsertAsync(account);
        var harness = new ClassificationHarness(storage, temp, account);
        harness.Inbox = await storage.Folders.UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox, InboxUidValidity));
        if (withJunk)
        {
            harness.Junk = await storage.Folders.UpsertAsync(TestData.NewFolder(account.Id, "Junk", FolderRole.Junk, JunkUidValidity));
        }

        if (withScreener)
        {
            harness.Screener = await storage.Folders.UpsertAsync(
                new MailFolder(0, account.Id, "Screener", FolderRole.Screener, FakeFolderProvisioner.ScreenerUidValidity));
        }

        if (withQuarantine)
        {
            harness.Quarantine = await storage.Folders.UpsertAsync(
                new MailFolder(0, account.Id, "Quarantine", FolderRole.Quarantine, FakeFolderProvisioner.QuarantineUidValidity));
        }

        if (withSent)
        {
            harness.Sent = await storage.Folders.UpsertAsync(
                TestData.NewFolder(account.Id, "Sent", FolderRole.Sent, SentUidValidity));
        }

        if (withContact)
        {
            await harness.AddContactAsync(ClassificationTestEml.ContactAddress);
        }

        if (withSyncState)
        {
            await harness.MarkSyncedAsync(harness.Inbox);
            if (harness.Junk is not null)
            {
                await harness.MarkSyncedAsync(harness.Junk);
            }

            if (harness.Quarantine is not null)
            {
                await harness.MarkSyncedAsync(harness.Quarantine);
            }

            if (harness.Sent is not null)
            {
                await harness.MarkSyncedAsync(harness.Sent);
            }
        }

        return harness;
    }

    /// <summary>Stores a completed sync state for the folder (the pass classifies only such folders).</summary>
    public Task MarkSyncedAsync(MailFolder folder) =>
        SyncStates.SaveAsync(new FolderSyncState(folder.Id, 0, 0, Clock.GetUtcNow()));

    /// <summary>Message key as the pass builds it: the top-most Received of the stored EML (none when unreadable).</summary>
    public async Task<string> KeyAsync(MailMessage message)
    {
        string? received = null;
        if (message.EmlPath is not null && Eml.Exists(message.EmlPath))
        {
            await using Stream stream = await Eml.OpenReadAsync(message.EmlPath);
            received = (await ClassificationInputReader.ReadMessageAsync(stream)).TopReceived;
        }

        return MessageKey.For(message, received);
    }

    public Task<Contact> AddContactAsync(string address) =>
        Contacts.UpsertAsync(new Contact(0, address, "Contact", ContactSource.Manual));

    /// <summary>Policy "Act" with the given retention.</summary>
    public static ClassificationPolicy ActWithRetention(int days) => new(ClassificationMode.Act, days);

    /// <summary>Policy "Act" with the given move limit per folder and pass (the brake of AP-07d), retention 0.</summary>
    public static ClassificationPolicy ActWithLimit(int maxMovesPerPass) => Act with { MaxMovesPerPass = maxMovesPerPass };

    public ClassificationPass CreatePass(
        ClassificationPolicy policy,
        IEmlStore? eml = null,
        IMailCommandQueue? queue = null,
        IMailFolderProvisioner? provisioner = null,
        IReadOnlyList<ISpamSignal>? signals = null) =>
        new(
            Storage.Accounts,
            Storage.Folders,
            SyncStates,
            Storage.Messages,
            eml ?? Eml,
            Contacts,
            SenderLists,
            Log,
            queue ?? Queue,
            provisioner ?? Provisioner,
            ClassifierSettings.Default,
            policy,
            Clock,
            signals);

    /// <summary>Harvester over the harness stores; the contact and EML store can be replaced (decorators).</summary>
    public ContactHarvester CreateHarvester(IContactStore? contacts = null, IEmlStore? eml = null) =>
        new(
            Storage.Accounts,
            Storage.Folders,
            SyncStates,
            Storage.Messages,
            eml ?? Eml,
            contacts ?? Contacts,
            HarvestMarkers,
            Clock);

    public Task<ContactHarvestReport> HarvestAsync(CancellationToken ct = default) =>
        CreateHarvester().RunAsync(Account.Id, ct);

    /// <summary>
    /// Stores a sent mail (raw headers only plus a short body) with its EML file. Each argument is the value of the
    /// header line, null omits the header.
    /// </summary>
    public Task<MailMessage> AddSentAsync(
        MailFolder folder,
        uint uid,
        string? to = null,
        string? cc = null,
        string? bcc = null)
    {
        var text = new StringBuilder();
        text.Append("MIME-Version: 1.0
");
        text.Append("From: Own A <").Append(Account.EmailAddress).Append(">
");
        foreach ((string name, string? value) in new[] { ("To", to), ("Cc", cc), ("Bcc", bcc) })
        {
            if (value is not null)
            {
                text.Append(name).Append(": ").Append(value).Append("
");
            }
        }

        text.Append("Subject: sample_subject
");
        text.Append("Date: ").Append(ClassificationTestEml.DateHeader).Append("
");
        text.Append("Message-ID: <sent_").Append(folder.Id).Append('_').Append(uid).Append("@example.test>
");
        text.Append("Content-Type: text/plain; charset=utf-8

body");
        return AddRawAsync(folder, uid, Encoding.UTF8.GetBytes(text.ToString()), $"<sent_{folder.Id}_{uid}@example.test>");
    }

    /// <summary>Address and name of every stored contact, ordered by address, for compact assertions.</summary>
    public async Task<IReadOnlyList<Contact>> ContactsAsync() =>
        [.. (await Contacts.ListAsync()).OrderBy(contact => contact.EmailAddress, StringComparer.OrdinalIgnoreCase)];

    public Task<ClassificationPassReport> RunAsync(ClassificationPolicy policy, CancellationToken ct = default) =>
        CreatePass(policy).RunAsync(Account.Id, ct);

    /// <summary>Stores a message of a fixture kind with its EML file. The Message-ID of the row is generated if not given.</summary>
    public Task<MailMessage> AddMailAsync(
        MailFolder folder,
        uint uid,
        MailKind kind,
        string? messageId = null,
        DateTimeOffset? date = null,
        string? received = null) =>
        AddRawAsync(folder, uid, ClassificationTestEml.For(kind, received), messageId ?? $"<{kind}_{folder.Id}_{uid}@example.test>", date);

    /// <summary>
    /// Stores a message row. With <paramref name="eml"/> the file is written to the standard path (or to
    /// <paramref name="sharedEmlPath"/> once); null leaves <c>EmlPath</c> null.
    /// </summary>
    public async Task<MailMessage> AddRawAsync(
        MailFolder folder,
        uint uid,
        byte[]? eml,
        string? messageId,
        DateTimeOffset? date = null,
        string? sharedEmlPath = null)
    {
        string? path = null;
        if (eml is not null)
        {
            path = sharedEmlPath ?? Eml.RelativePathFor(Account.Id, folder.Id, uid);
            if (!Eml.Exists(path))
            {
                await Eml.WriteAsync(path, new MemoryStream(eml));
            }
        }

        return await Storage.Messages.UpsertAsync(
            TestData.NewMessage(Account.Id, folder.Id, uid, messageId: messageId, date: date, emlPath: path),
            bodyText: null);
    }

    /// <summary>Stores a message row whose <c>EmlPath</c> points to a file that does not exist.</summary>
    public Task<MailMessage> AddMessageWithMissingFileAsync(MailFolder folder, uint uid, string messageId) =>
        Storage.Messages.UpsertAsync(
            TestData.NewMessage(
                Account.Id, folder.Id, uid, messageId: messageId, emlPath: Eml.RelativePathFor(Account.Id, folder.Id, uid)),
            bodyText: null);

    /// <summary>Highest local UID of the folder, 0 when empty.</summary>
    public async Task<uint> HighestUidAsync(MailFolder folder)
    {
        IReadOnlyDictionary<uint, MessageFlags> uids = await Storage.Messages.GetUidFlagsAsync(folder.Id);
        return uids.Count == 0 ? 0 : uids.Keys.Max();
    }

    /// <summary>Sets the folder's marker to its current UIDVALIDITY and highest local UID (existing stock is "known").</summary>
    public async Task<ClassificationMarker> SeedMarkerAsync(MailFolder folder)
    {
        var marker = new ClassificationMarker(folder.Id, folder.UidValidity, await HighestUidAsync(folder));
        await Log.CommitAsync([], marker);
        return marker;
    }

    public Task<ClassificationMarker?> MarkerAsync(MailFolder folder) => Log.GetMarkerAsync(folder.Id);

    public Task<IReadOnlyList<MailCommand>> CommandsAsync() => Queue.ListAsync(Account.Id);

    public async Task<ClassificationLogEntry?> EntryAsync(MailMessage message) =>
        await Log.GetAsync(Account.Id, await KeyAsync(message));

    public async Task<ClassificationLogEntry> RequireEntryAsync(MailMessage message) =>
        await EntryAsync(message) ?? throw new Xunit.Sdk.XunitException($"No log entry for uid {message.Uid}.");

    public Task<long> LogCountAsync() => Storage.CountRowsAsync("classification_log");

    /// <summary>Stores a log entry for an existing message (used to seed retention scenarios); keeps the Inbox marker valid.</summary>
    public async Task<ClassificationLogEntry> SeedEntryAsync(
        MailMessage message,
        DateTimeOffset classifiedAt,
        ClassificationOutcome outcome = ClassificationOutcome.Enqueued,
        ClassificationAction action = ClassificationAction.MoveToQuarantine,
        string? keyOverride = null)
    {
        var entry = new ClassificationLogEntry(
            Account.Id,
            keyOverride ?? await KeyAsync(message),
            Inbox.Id,
            message.Uid,
            classifiedAt,
            ClassificationMode.Act,
            outcome,
            action,
            SpamTier.CertainSpam,
            110,
            AuthStatus.Failed,
            null,
            [new SignalResult("authentication", 50, "Seeded reason.")]);
        await Log.CommitAsync([entry], new ClassificationMarker(Inbox.Id, Inbox.UidValidity, await HighestUidAsync(Inbox)));
        return entry;
    }

    /// <summary>All cells of both classification tables as one text, for "no marker text anywhere" assertions.</summary>
    public async Task<string> DumpClassificationTablesAsync()
    {
        var text = new StringBuilder();
        foreach (string table in new[] { "classification_log", "classification_marker" })
        {
            await using SqliteCommand command = Storage.Database.Connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table}";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    text.Append(reader.GetValue(column)).Append('|');
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>The UIDs of all commands of a kind with the given source folder and target, in queue order.</summary>
    public static IEnumerable<MailCommand> MovesFrom(IEnumerable<MailCommand> commands, MailFolder source, MailFolder target) =>
        commands.Where(command => command.Kind == MailCommandKind.Move
            && command.FolderId == source.Id && command.TargetFolderId == target.Id);

    public async ValueTask DisposeAsync()
    {
        await Storage.DisposeAsync();
        temp.Dispose();
    }
}

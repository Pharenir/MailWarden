using System.Collections.Frozen;
using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;
using MailWarden.Core.Sync;
using MailWarden.Infrastructure.Mime;
using MimeKit;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Fills the address book from the recipients of an account's sent mail. Reads the header block of each EML only.
/// </summary>
/// <remarks>
/// <para>
/// Spoofing: only folders with the role <see cref="FolderRole.Sent"/> count, so an incoming mail cannot create a contact. A
/// contact alone never trusts a mail either: the tier <see cref="SpamTier.TrustedContact"/> also needs passing authentication.
/// </para>
/// <para>
/// Not everything in a Sent folder was written by the user. A mail counts as skipped when From is not exactly one account
/// address (so an alias delivers no contacts, which is the safe side), when <c>Auto-Submitted</c> is present and not
/// <c>no</c>, or when the top Content-Type is <c>multipart/report</c> (delivery reports and the like).
/// </para>
/// <para>
/// A folder is only handled after a completed sync (it has a stored sync state). Unlike the classification pass, the first
/// run reads the whole stock, because it is exactly what the user wrote. The marker is committed per block of at most 200
/// mails, so a long first run can continue after a cancellation; a block that is read again adds nothing twice, because an
/// existing address is never changed.
/// </para>
/// <para>
/// No report or exception of this class contains an address or a name. Not thread-safe; one run at a time.
/// </para>
/// </remarks>
public sealed class ContactHarvester
{
    private const int MailsPerMarkerBlock = 200;

    // Local parts of addresses that nobody reads. Compared without regard to case.
    private static readonly FrozenSet<string> RoleLocalParts = new[]
    {
        "noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IAccountStore accounts;
    private readonly IFolderStore folders;
    private readonly IFolderSyncStateStore syncStates;
    private readonly IMessageStore messages;
    private readonly IEmlStore emlStore;
    private readonly IContactStore contacts;
    private readonly IContactHarvestMarkerStore markers;

    /// <summary>Creates the harvester; nothing runs before <see cref="RunAsync"/>.</summary>
    /// <param name="accounts">Account store; also the source of the own addresses.</param>
    /// <param name="folders">Folder store.</param>
    /// <param name="syncStates">Sync state per folder; a folder without one has no completed sync yet.</param>
    /// <param name="messages">Message store.</param>
    /// <param name="emlStore">Store of the raw messages.</param>
    /// <param name="contacts">Address book.</param>
    /// <param name="markers">Harvest marker per folder.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public ContactHarvester(
        IAccountStore accounts,
        IFolderStore folders,
        IFolderSyncStateStore syncStates,
        IMessageStore messages,
        IEmlStore emlStore,
        IContactStore contacts,
        IContactHarvestMarkerStore markers)
    {
        this.accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        this.folders = folders ?? throw new ArgumentNullException(nameof(folders));
        this.syncStates = syncStates ?? throw new ArgumentNullException(nameof(syncStates));
        this.messages = messages ?? throw new ArgumentNullException(nameof(messages));
        this.emlStore = emlStore ?? throw new ArgumentNullException(nameof(emlStore));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.markers = markers ?? throw new ArgumentNullException(nameof(markers));
    }

    /// <summary>Harvests the new mail of all Sent folders of the account.</summary>
    /// <param name="accountId">Account to handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The counts of this run.</returns>
    /// <exception cref="ArgumentException">The account is unknown.</exception>
    /// <exception cref="StorageException">A database store failed. A missing or unreadable EML is no failure.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled; finished blocks stay committed.</exception>
    public async Task<ContactHarvestReport> RunAsync(Guid accountId, CancellationToken ct = default)
    {
        Account account = await accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new ArgumentException($"Unknown account id {accountId}.", nameof(accountId));
        IReadOnlyList<MailFolder> accountFolders = await folders.ListAsync(account.Id, ct).ConfigureAwait(false);

        var state = new RunState();
        foreach (MailFolder folder in accountFolders.Where(folder => folder.Role == FolderRole.Sent && folder.UidValidity != 0))
        {
            ct.ThrowIfCancellationRequested();
            await HandleFolderAsync(folder, state, ct).ConfigureAwait(false);
        }

        return new ContactHarvestReport(state.MailsRead, state.ContactsAdded, state.MailsSkipped);
    }

    // Own addresses are no contacts; the local part decides about a role address.
    private static bool IsCollectable(string address, HashSet<string> ownAddresses)
    {
        return AddressRules.IsWellFormedAddress(address)
            && !ownAddresses.Contains(address)
            && !RoleLocalParts.Contains(address[..address.IndexOf('@', StringComparison.Ordinal)]);
    }

    private static bool IsWrittenByUser(HeaderList headers, ParserOptions options, HashSet<string> ownAddresses)
    {
        List<Header> fromHeaders = [.. headers.Where(header => header.Id == HeaderId.From)];
        if (fromHeaders.Count != 1
            || !InternetAddressList.TryParse(options, fromHeaders[0].RawValue, out InternetAddressList? fromAddresses))
        {
            return false;
        }

        List<MailboxAddress> senders = [.. fromAddresses.Mailboxes];
        return senders.Count == 1
            && ownAddresses.Contains(senders[0].Address?.Trim() ?? string.Empty)
            && !headers.Any(header => IsAutomaticMarker(header, options));
    }

    private static bool IsAutomaticMarker(Header header, ParserOptions options)
    {
        if (header.Id == HeaderId.AutoSubmitted)
        {
            // RFC 3834: the value can carry parameters. The raw bytes, because Header.Value decodes encoded words.
            string value = Header.Unfold(Encoding.Latin1.GetString(header.RawValue));
            int parametersStart = value.IndexOf(';', StringComparison.Ordinal);
            string keyword = (parametersStart < 0 ? value : value[..parametersStart]).Trim();
            return !keyword.Equals("no", StringComparison.OrdinalIgnoreCase);
        }

        return header.Id == HeaderId.ContentType
            && ContentType.TryParse(options, header.RawValue, out ContentType? contentType)
            && contentType.IsMimeType("multipart", "report");
    }

    // All mailboxes of To, Cc and Bcc, in header order; Mailboxes also walks through groups. The header value is parsed raw,
    // like MimeMessage does.
    private static List<MailboxAddress> ReadRecipients(HeaderList headers, ParserOptions options)
    {
        var recipients = new List<MailboxAddress>();
        foreach (Header header in headers.Where(header => header.Id is HeaderId.To or HeaderId.Cc or HeaderId.Bcc))
        {
            if (InternetAddressList.TryParse(options, header.RawValue, out InternetAddressList? addresses))
            {
                recipients.AddRange(addresses.Mailboxes);
            }
            else
            {
                AddRecipientsOfBrokenList(header.RawValue, options, recipients);
            }
        }

        return recipients;
    }

    // One malformed address makes the parser give up on the whole list, and it must not cost the good ones next to it. So each
    // piece between the list separators (comma, group start, group end) outside quotes is tried alone.
    private static void AddRecipientsOfBrokenList(byte[] rawValue, ParserOptions options, List<MailboxAddress> recipients)
    {
        int pieceStart = 0;
        bool isInQuotes = false;
        for (int index = 0; index <= rawValue.Length; index++)
        {
            if (index < rawValue.Length && rawValue[index] == (byte)'"')
            {
                isInQuotes = !isInQuotes;
            }

            bool isPieceEnd = index == rawValue.Length
                || (!isInQuotes && rawValue[index] is (byte)',' or (byte)':' or (byte)';');
            if (!isPieceEnd)
            {
                continue;
            }

            if (index > pieceStart
                && InternetAddressList.TryParse(options, rawValue, pieceStart, index - pieceStart, out InternetAddressList? addresses))
            {
                recipients.AddRange(addresses.Mailboxes);
            }

            pieceStart = index + 1;
        }
    }

    private async Task HandleFolderAsync(MailFolder folder, RunState state, CancellationToken ct)
    {
        // The sync stores UIDVALIDITY before its first fetch and the sync state only after the folder completed.
        if (await syncStates.GetAsync(folder.Id, ct).ConfigureAwait(false) is null)
        {
            return;
        }

        // No marker, or the folder's UIDs were reassigned: from the start.
        ContactHarvestMarker? marker = await markers.GetAsync(folder.Id, ct).ConfigureAwait(false);
        uint harvestedUpToUid = marker is not null && marker.UidValidity == folder.UidValidity ? marker.HarvestedUpToUid : 0;

        IReadOnlyDictionary<uint, string?> localMessages = await messages.GetUidEmlPathsAsync(folder.Id, ct).ConfigureAwait(false);
        List<uint> newUids = [.. localMessages.Keys.Where(uid => uid > harvestedUpToUid).Order()];
        if (newUids.Count == 0)
        {
            return;
        }

        await state.LoadAddressesAsync(accounts, contacts, ct).ConfigureAwait(false);
        foreach (uint[] block in newUids.Chunk(MailsPerMarkerBlock))
        {
            foreach (uint uid in block)
            {
                ct.ThrowIfCancellationRequested();
                await HarvestMailAsync(localMessages[uid], state, ct).ConfigureAwait(false);
            }

            await markers
                .SaveAsync(new ContactHarvestMarker(folder.Id, folder.UidValidity, block[^1]), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task HarvestMailAsync(string? emlPath, RunState state, CancellationToken ct)
    {
        List<MailboxAddress>? recipients = await TryReadRecipientsAsync(emlPath, state.OwnAddresses, ct).ConfigureAwait(false);
        if (recipients is null)
        {
            state.MailsSkipped++;
            return;
        }

        state.MailsRead++;
        foreach (MailboxAddress recipient in recipients)
        {
            string address = recipient.Address?.Trim() ?? string.Empty;
            if (!IsCollectable(address, state.OwnAddresses) || !state.KnownContacts.Add(address))
            {
                continue;
            }

            var contact = new Contact(0, address, ContactNames.Clean(recipient.Name), ContactSource.AutoCollected);
            await contacts.AddIfMissingAsync(contact, ct).ConfigureAwait(false);
            state.ContactsAdded++;
        }
    }

    // Null when the mail has no EML, the bytes are no message or the user did not write it. Exception messages can quote message
    // data and are not kept. The catch only spans the EML: failures of the database stores propagate from the callers.
    private async Task<List<MailboxAddress>?> TryReadRecipientsAsync(string? emlPath, HashSet<string> ownAddresses, CancellationToken ct)
    {
        if (emlPath is null)
        {
            return null;
        }

        try
        {
            ParserOptions options = MimeParserDefaults.Create();
            await using Stream eml = await emlStore.OpenReadAsync(emlPath, ct).ConfigureAwait(false);
            HeaderList headers = await HeaderList.LoadAsync(options, eml, ct).ConfigureAwait(false);
            return headers.Count > 0 && IsWrittenByUser(headers, options, ownAddresses) ? ReadRecipients(headers, options) : null;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return null;
        }
    }

    // Everything one run carries from folder to folder. The addresses are loaded when the first new mail is due, because most
    // cycles have none.
    private sealed class RunState
    {
        public HashSet<string> OwnAddresses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> KnownContacts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int MailsRead { get; set; }

        public int ContactsAdded { get; set; }

        public int MailsSkipped { get; set; }

        private bool IsLoaded { get; set; }

        public async Task LoadAddressesAsync(IAccountStore accounts, IContactStore contacts, CancellationToken ct)
        {
            if (IsLoaded)
            {
                return;
            }

            foreach (Account account in await accounts.ListAsync(ct).ConfigureAwait(false))
            {
                OwnAddresses.Add(account.EmailAddress);
            }

            foreach (Contact contact in await contacts.ListAsync(ct).ConfigureAwait(false))
            {
                KnownContacts.Add(contact.EmailAddress);
            }

            IsLoaded = true;
        }
    }
}

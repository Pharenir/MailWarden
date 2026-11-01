using System.Text;
using MailWarden.Core.Classification;
using MailWarden.Core.Model;
using MailWarden.Core.Storage;

namespace MailWarden.Infrastructure.Classification;

/// <summary>
/// Imports contacts from a vCard or CSV file the user chose. The caller opens the stream; this class never takes a file path
/// from foreign data. The whole file is parsed before the first contact is stored, so a file that is rejected stores nothing.
/// An existing address stays unchanged.
/// </summary>
/// <remarks>
/// Text is UTF-8 (with or without BOM) or UTF-16 with BOM. Bytes that are no valid UTF-8 become U+FFFD: a name then shows it, an
/// address with it counts as rejected. Nothing is logged, and exception messages of this class never quote a value of the file.
/// </remarks>
public static class ContactImportFile
{
    private const int MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxEntries = 50_000;
    private const int ReadBufferBytes = 81_920;
    private const string VCardBegin = "BEGIN";
    private const string VCardEnd = "END";
    private const string VCardObjectName = "VCARD";
    private const string VCardEmail = "EMAIL";
    private const string VCardFullName = "FN";

    private static readonly string[] CsvEmailColumns = ["Email", "E-Mail", "E-Mail Address"];

    /// <summary>
    /// Imports a vCard file with <see cref="ContactSource.Imported"/>. Folded lines (RFC 6350) are unfolded; several <c>EMAIL</c>
    /// lines of a card become several contacts, the name comes from <c>FN</c>. A card without an address, and a card that does
    /// not end, count as one rejected entry. Reads at most 10 MB; entries above the first 50,000 count as rejected.
    /// </summary>
    /// <param name="file">File content, read from its current position; not disposed.</param>
    /// <param name="contacts">Address book.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The counts of the import.</returns>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="InvalidDataException">The file is larger than 10 MB or holds no vCard; nothing was stored then.</exception>
    /// <exception cref="OperationCanceledException">The import was cancelled; contacts stored so far stay.</exception>
    public static async Task<ContactImportReport> ImportVCardAsync(Stream file, IContactStore contacts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(contacts);

        string text = await ReadTextAsync(file, ct).ConfigureAwait(false);
        return await StoreAsync(ParseVCards(text), contacts, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports a CSV file with <see cref="ContactSource.Imported"/>. A header row is required; column names are case-insensitive:
    /// Email, E-Mail, E-Mail Address, Name, Display Name, First Name, Last Name. The separator <c>,</c> or <c>;</c> is detected
    /// from the header, quotes follow RFC 4180. The name is Display Name, else Name, else First Name and Last Name. Blank lines
    /// are no entries; a row without an address counts as rejected. Reads at most 10 MB; entries above the first 50,000 count
    /// as rejected.
    /// </summary>
    /// <param name="file">File content, read from its current position; not disposed.</param>
    /// <param name="contacts">Address book.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The counts of the import.</returns>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="InvalidDataException">The file is larger than 10 MB or has no usable header; nothing was stored then.</exception>
    /// <exception cref="OperationCanceledException">The import was cancelled; contacts stored so far stay.</exception>
    public static async Task<ContactImportReport> ImportCsvAsync(Stream file, IContactStore contacts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(contacts);

        string text = await ReadTextAsync(file, ct).ConfigureAwait(false);
        return await StoreAsync(ParseCsv(text), contacts, ct).ConfigureAwait(false);
    }

    // The stream may not know its length, so the limit is checked while reading.
    private static async Task<string> ReadTextAsync(Stream file, CancellationToken ct)
    {
        using var content = new MemoryStream();
        byte[] readBuffer = new byte[ReadBufferBytes];
        int readCount;
        while ((readCount = await file.ReadAsync(readBuffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (content.Length + readCount > MaxFileBytes)
            {
                throw new InvalidDataException("The import file is larger than 10 MB.");
            }

            content.Write(readBuffer, 0, readCount);
        }

        return Decode(content.GetBuffer().AsSpan(0, (int)content.Length));
    }

    private static string Decode(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith<byte>([0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(content[2..]);
        }

        if (content.StartsWith<byte>([0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(content[2..]);
        }

        ReadOnlySpan<byte> withoutMark = content.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? content[3..] : content;
        return Encoding.UTF8.GetString(withoutMark);
    }

    // Every entry counts once, so Read is always the sum of the others. An address that repeats in the file is known by then.
    // The address book is compared in memory, because the store's answer does not tell whether it added.
    private static async Task<ContactImportReport> StoreAsync(List<ImportEntry> entries, IContactStore contacts, CancellationToken ct)
    {
        var knownAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Contact contact in await contacts.ListAsync(ct).ConfigureAwait(false))
        {
            knownAddresses.Add(contact.EmailAddress);
        }

        int read = 0;
        int added = 0;
        int alreadyKnown = 0;
        int rejected = 0;
        foreach (ImportEntry entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            read++;
            string address = entry.Address.Trim();
            if (read > MaxEntries || !AddressRules.IsWellFormedAddress(address))
            {
                rejected++;
            }
            else if (!knownAddresses.Add(address))
            {
                alreadyKnown++;
            }
            else
            {
                var contact = new Contact(0, address, ContactNames.Clean(entry.Name), ContactSource.Imported);
                await contacts.AddIfMissingAsync(contact, ct).ConfigureAwait(false);
                added++;
            }
        }

        return new ContactImportReport(read, added, alreadyKnown, rejected);
    }

    private static List<ImportEntry> ParseVCards(string text)
    {
        var entries = new List<ImportEntry>();
        bool foundCard = false;
        VCard? openCard = null;
        foreach (string line in ReadUnfoldedLines(text))
        {
            if (!TryParseVCardLine(line, out string propertyName, out string value))
            {
                continue;
            }

            bool isCardObject = string.Equals(value.Trim(), VCardObjectName, StringComparison.OrdinalIgnoreCase);
            if (isCardObject && IsProperty(propertyName, VCardBegin))
            {
                // The card before this one never ended.
                if (openCard is not null)
                {
                    entries.Add(ImportEntry.Malformed);
                }

                openCard = new VCard();
                foundCard = true;
            }
            else if (isCardObject && IsProperty(propertyName, VCardEnd))
            {
                openCard?.AddEntriesTo(entries);
                openCard = null;
            }
            else if (openCard is not null && IsProperty(propertyName, VCardEmail))
            {
                openCard.Addresses.Add(UnescapeVCardValue(value));
            }
            else if (openCard is not null && IsProperty(propertyName, VCardFullName))
            {
                openCard.Name ??= UnescapeVCardValue(value);
            }
        }

        if (!foundCard)
        {
            throw new InvalidDataException("The import file holds no vCard: no BEGIN:VCARD line was found.");
        }

        if (openCard is not null)
        {
            entries.Add(ImportEntry.Malformed);
        }

        return entries;
    }

    private static bool IsProperty(string propertyName, string expected)
    {
        return string.Equals(propertyName, expected, StringComparison.OrdinalIgnoreCase);
    }

    // RFC 6350, 3.2: a line that starts with a space or a tab continues the line before, without that one character.
    private static IEnumerable<string> ReadUnfoldedLines(string text)
    {
        using var reader = new StringReader(text);
        StringBuilder? logicalLine = null;
        while (reader.ReadLine() is string line)
        {
            if (line.Length > 0 && line[0] is ' ' or '\t')
            {
                logicalLine?.Append(line, 1, line.Length - 1);
                continue;
            }

            if (logicalLine is not null)
            {
                yield return logicalLine.ToString();
            }

            logicalLine = new StringBuilder(line);
        }

        if (logicalLine is not null)
        {
            yield return logicalLine.ToString();
        }
    }

    // "[group.]NAME[;parameters]:value"; a parameter value in quotes may hold a colon.
    private static bool TryParseVCardLine(string line, out string propertyName, out string value)
    {
        propertyName = string.Empty;
        value = string.Empty;

        bool isInQuotes = false;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '"')
            {
                isInQuotes = !isInQuotes;
            }
            else if (line[index] == ':' && !isInQuotes)
            {
                string head = line[..index];
                int parametersStart = head.IndexOf(';', StringComparison.Ordinal);
                string nameWithGroup = parametersStart < 0 ? head : head[..parametersStart];
                propertyName = nameWithGroup[(nameWithGroup.LastIndexOf('.') + 1)..].Trim();
                value = line[(index + 1)..];
                return true;
            }
        }

        return false;
    }

    // RFC 6350, 3.4: a backslash escapes comma, semicolon, colon and backslash, and "\n" is a line break.
    private static string UnescapeVCardValue(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var unescaped = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index == value.Length - 1)
            {
                unescaped.Append(value[index]);
                continue;
            }

            index++;
            unescaped.Append(value[index] is 'n' or 'N' ? '\n' : value[index]);
        }

        return unescaped.ToString();
    }

    private static List<ImportEntry> ParseCsv(string text)
    {
        char separator = DetectCsvSeparator(text);
        var entries = new List<ImportEntry>();
        CsvColumns? columns = null;
        foreach (CsvRow row in ReadCsvRows(text, separator))
        {
            if (row.IsBlank)
            {
                continue;
            }

            if (columns is null)
            {
                columns = CsvColumns.FromHeader(row);
                continue;
            }

            entries.Add(row.IsComplete ? new ImportEntry(columns.GetAddress(row), columns.GetName(row)) : ImportEntry.Malformed);
        }

        if (columns is null)
        {
            throw new InvalidDataException("The import file has no usable header: the file is empty.");
        }

        return entries;
    }

    // From the first line that is not empty, outside quotes. A header with one column has no separator at all.
    private static char DetectCsvSeparator(string text)
    {
        int commaCount = 0;
        int semicolonCount = 0;
        bool isInQuotes = false;
        foreach (char character in text.AsSpan().TrimStart("\r\n"))
        {
            if (character == '"')
            {
                isInQuotes = !isInQuotes;
            }
            else if (!isInQuotes && character is '\r' or '\n')
            {
                break;
            }
            else if (!isInQuotes && character == ',')
            {
                commaCount++;
            }
            else if (!isInQuotes && character == ';')
            {
                semicolonCount++;
            }
        }

        return semicolonCount > commaCount ? ';' : ',';
    }

    // RFC 4180. A quote only opens at the start of a field and "" inside quotes is one quote; a line break inside quotes stays
    // in the field. A text that ends inside quotes gives a last row that is not complete.
    private static IEnumerable<CsvRow> ReadCsvRows(string text, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool isInQuotes = false;
        bool hasOpenRow = false;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            hasOpenRow = true;
            if (isInQuotes)
            {
                if (character != '"')
                {
                    field.Append(character);
                }
                else if (index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    isInQuotes = false;
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                isInQuotes = true;
            }
            else if (character == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                fields.Add(field.ToString());
                field.Clear();
                yield return new CsvRow(fields, true);
                fields = [];
                hasOpenRow = false;
            }
            else
            {
                field.Append(character);
            }
        }

        if (hasOpenRow)
        {
            fields.Add(field.ToString());
            yield return new CsvRow(fields, !isInQuotes);
        }
    }

    // One address with its name. An empty address stands for an entry that is malformed as a whole.
    private readonly record struct ImportEntry(string Address, string Name)
    {
        public static ImportEntry Malformed { get; } = new(string.Empty, string.Empty);
    }

    // What one card has collected up to its END line.
    private sealed class VCard
    {
        public List<string> Addresses { get; } = [];

        public string? Name { get; set; }

        public void AddEntriesTo(List<ImportEntry> entries)
        {
            if (Addresses.Count == 0)
            {
                entries.Add(ImportEntry.Malformed);
            }

            foreach (string address in Addresses)
            {
                entries.Add(new ImportEntry(address, Name ?? string.Empty));
            }
        }
    }

    private sealed record CsvRow(List<string> Fields, bool IsComplete)
    {
        public bool IsBlank => IsComplete && Fields.Count == 1 && Fields[0].Length == 0;

        public string GetField(int? column)
        {
            return column is int index && index < Fields.Count ? Fields[index].Trim() : string.Empty;
        }
    }

    // Positions of the known columns in the header. Columns with other names are ignored.
    private sealed class CsvColumns
    {
        private readonly Dictionary<string, int> positions;

        private CsvColumns(Dictionary<string, int> positions)
        {
            this.positions = positions;
        }

        public static CsvColumns FromHeader(CsvRow header)
        {
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (header.IsComplete)
            {
                for (int index = 0; index < header.Fields.Count; index++)
                {
                    positions.TryAdd(header.Fields[index].Trim(), index);
                }
            }

            if (!CsvEmailColumns.Any(positions.ContainsKey))
            {
                throw new InvalidDataException("The import file has no usable header: no e-mail column was found.");
            }

            return new CsvColumns(positions);
        }

        // The first e-mail column of the row that holds anything.
        public string GetAddress(CsvRow row)
        {
            return CsvEmailColumns.Select(column => GetValue(row, column)).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        }

        public string GetName(CsvRow row)
        {
            string displayName = GetValue(row, "Display Name");
            if (displayName.Length > 0)
            {
                return displayName;
            }

            string name = GetValue(row, "Name");
            return name.Length > 0 ? name : $"{GetValue(row, "First Name")} {GetValue(row, "Last Name")}".Trim();
        }

        private string GetValue(CsvRow row, string column)
        {
            return row.GetField(positions.TryGetValue(column, out int position) ? position : null);
        }
    }
}

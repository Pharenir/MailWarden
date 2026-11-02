using System.Text;
using MailWarden.Infrastructure.Composition;
using MailWarden.Infrastructure.Mime;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Tests.Composition;

/// <summary>
/// AP-04c cases 8 and 9 (A9): <see cref="IMailboxReadModel.CopyAttachmentAsync"/> of the real runtime, against EML files
/// stored like the sync does. Content is artificial; the raw parts fix the transfer encodings exactly.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:storage")]
[Trait("Category", "feature:sync")]
public sealed class AttachmentCopyTests : IAsyncLifetime
{
    private const string Headers =
        "From: Sender A <sender_a@example.test>\r\n" +
        "To: Recipient B <recipient_b@example.test>\r\n" +
        "Subject: attachment_subject\r\n" +
        "Date: Mon, 15 Jun 2026 10:00:00 +0200\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=\"bx\"\r\n\r\n";

    private static readonly byte[] AllByteValues = [.. Enumerable.Range(0, 256).Select(value => (byte)value)];
    private static readonly byte[] QuotedPrintableDecoded = Encoding.UTF8.GetBytes("Grüße = 1 + 1");
    private static readonly byte[] EightBitDecoded = Encoding.UTF8.GetBytes("Größe acht bit");

    private readonly string _baseDirectory = Path.Combine(AppContext.BaseDirectory, "ap04c-copy", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (MailWardenRuntime runtime in _runtimes)
        {
            await runtime.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was created.
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private static string TextPart() => "--bx\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nbody text\r\n";

    private static string Base64Part(string name, byte[] content) =>
        $"--bx\r\nContent-Type: application/octet-stream; name=\"{name}\"\r\nContent-Disposition: attachment; filename=\"{name}\"\r\n" +
        "Content-Transfer-Encoding: base64\r\n\r\n" +
        Convert.ToBase64String(content, Base64FormattingOptions.InsertLineBreaks) + "\r\n";

    /// <summary>The quoted-printable and the 8bit part are written as text, so the encoding on the wire is fixed.</summary>
    private static string QuotedPrintablePart(string name) =>
        $"--bx\r\nContent-Type: text/plain; charset=utf-8; name=\"{name}\"\r\nContent-Disposition: attachment; filename=\"{name}\"\r\n" +
        "Content-Transfer-Encoding: quoted-printable\r\n\r\nGr=C3=BC=C3=9Fe =3D 1 + 1\r\n";

    private static string EightBitPart(string name) =>
        $"--bx\r\nContent-Type: text/plain; charset=utf-8; name=\"{name}\"\r\nContent-Disposition: attachment; filename=\"{name}\"\r\n" +
        "Content-Transfer-Encoding: 8bit\r\n\r\nGröße acht bit\r\n";

    private static byte[] Eml(params string[] parts) => Encoding.UTF8.GetBytes(Headers + TextPart() + string.Concat(parts) + "--bx--\r\n");

    private sealed record Stored(MailWardenRuntime Runtime, DataPaths Paths, MailMessage Message, string EmlPath);

    private async Task<Stored> StoreAsync(byte[]? eml, bool writeFile = true, bool withPath = true)
    {
        string root = Path.Combine(_baseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new DataPaths(root);
        MailWardenRuntime runtime = await MailWardenRuntime.OpenAsync(paths);
        _runtimes.Add(runtime);
        SqliteDatabase database = await runtime.OpenDatabaseAsync();
        Account account = TestData.NewAccount("Account_A");
        await new SqliteAccountStore(database).UpsertAsync(account);
        MailFolder inbox = await new SqliteFolderStore(database).UpsertAsync(TestData.NewFolder(account.Id, "INBOX", FolderRole.Inbox));
        var emlStore = new FileEmlStore(paths);
        string emlPath = emlStore.RelativePathFor(account.Id, inbox.Id, 1);
        if (writeFile && eml is not null)
        {
            await emlStore.WriteAsync(emlPath, new MemoryStream(eml));
        }

        MailMessage stored = await new SqliteMessageStore(database).UpsertAsync(
            TestData.NewMessage(account.Id, inbox.Id, 1, emlPath: withPath ? emlPath : null), null);
        return new Stored(runtime, paths, stored, emlPath);
    }

    private readonly List<MailWardenRuntime> _runtimes = [];

    private static async Task<int> IndexOfAsync(Stored stored, string fileName) => (await ShownAsync(stored, fileName)).Index;

    /// <summary>Position and entry as the reading pane would hold them for the row of the file.</summary>
    private static async Task<(int Index, MailAttachmentInfo Info)> ShownAsync(Stored stored, string fileName)
    {
        MailContent? content = await stored.Runtime.ReadModel.ReadContentAsync(stored.Message);
        Assert.NotNull(content);
        int index = -1;
        for (int position = 0; position < content.Attachments.Count; position++)
        {
            if (content.Attachments[position].FileName == fileName)
            {
                index = position;
            }
        }

        Assert.True(index >= 0, $"attachment {fileName} is not listed");
        return (index, content.Attachments[index]);
    }

    private static async Task<byte[]> CopyAsync(Stored stored, string fileName)
    {
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, fileName);
        using var destination = new MemoryStream();
        await stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination);
        return destination.ToArray();
    }

    private static readonly MailAttachmentInfo AnyEntry = new("a.bin", "application/octet-stream", 1);

    // ---- Case 8 --------------------------------------------------------------------------------------------

    // Case 8: base64, quoted-printable and 8bit each give the decoded bytes, picked by position.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Base64_quoted_printable_and_8bit_parts_deliver_the_exact_bytes()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("all_bytes.bin", AllByteValues), QuotedPrintablePart("qp.txt"), EightBitPart("eight.txt")));

        Assert.Equal(AllByteValues, await CopyAsync(stored, "all_bytes.bin"));
        Assert.Equal(QuotedPrintableDecoded, await CopyAsync(stored, "qp.txt"));
        Assert.Equal(EightBitDecoded, await CopyAsync(stored, "eight.txt"));
    }

    // Case 8: a part of several megabytes (stream copy, more than one buffer) arrives complete.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_large_base64_part_arrives_complete()
    {
        byte[] large = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(large);
        Stored stored = await StoreAsync(Eml(Base64Part("large.bin", large)));

        byte[] copied = await CopyAsync(stored, "large.bin");

        Assert.Equal(large.Length, copied.Length);
        Assert.True(large.AsSpan().SequenceEqual(copied), "the copied bytes differ");
    }

    // Case 8: each index gives its own part.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Each_index_delivers_its_own_part()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("first.bin", [1, 1, 1]), Base64Part("second.bin", [2, 2]), Base64Part("third.bin", [3])));

        Assert.Equal(new byte[] { 1, 1, 1 }, await CopyAsync(stored, "first.bin"));
        Assert.Equal(new byte[] { 2, 2 }, await CopyAsync(stored, "second.bin"));
        Assert.Equal(new byte[] { 3 }, await CopyAsync(stored, "third.bin"));
    }

    // Case 8: no handle on the EML stays open after the copy (the sync may delete the file at any time).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task The_eml_is_not_held_open_after_the_copy()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1, 2, 3])));
        await CopyAsync(stored, "a.bin");

        string absolute = stored.Paths.ResolveMailPath(stored.EmlPath);
        File.Delete(absolute);

        Assert.False(File.Exists(absolute));
    }

    // Case 8 / A12: copying a part changes no flag.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Copying_a_part_does_not_change_the_stored_flags()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1, 2, 3])));
        SqliteDatabase database = await stored.Runtime.OpenDatabaseAsync();

        await CopyAsync(stored, "a.bin");

        MailMessage? reloaded = await new SqliteMessageStore(database).GetAsync(stored.Message.Id);
        Assert.Equal(MessageFlags.None, reloaded?.Flags);
    }

    // ---- Case 9 --------------------------------------------------------------------------------------------

    // Case 9: index outside the list.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task An_index_outside_the_list_throws_and_writes_nothing(int index)
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1]), Base64Part("b.bin", [2])));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, AnyEntry, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9: a mail without attachments has no valid index at all.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_mail_without_attachments_has_no_valid_index()
    {
        Stored stored = await StoreAsync(Eml());
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, 0, AnyEntry, destination));
    }

    // Case 9 / contract: no expected entry is a programming error.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_missing_expected_entry_throws_argument_null()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1])));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, 0, null!, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9: no EML on disk, or no path stored.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_missing_eml_throws_file_not_found(bool writeFile, bool withPath)
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1])), writeFile, withPath);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, 0, AnyEntry, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9: the EML was deleted after the mail was shown.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_eml_deleted_after_showing_the_mail_throws_file_not_found()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1])));
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, "a.bin");
        File.Delete(stored.Paths.ResolveMailPath(stored.EmlPath));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination));
    }

    // Case 9 (A9): the EML changed after the list was shown; the part at the index is another one now.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_changed_eml_with_swapped_parts_delivers_no_bytes_of_the_other_part()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("first.bin", [1, 1, 1]), Base64Part("second.bin", [2, 2, 2, 2, 2])));
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, "first.bin");
        await File.WriteAllBytesAsync(
            stored.Paths.ResolveMailPath(stored.EmlPath),
            Eml(Base64Part("second.bin", [2, 2, 2, 2, 2]), Base64Part("first.bin", [1, 1, 1])));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MailContentException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9 (A9): same name, other size.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_changed_eml_with_the_same_name_and_another_size_throws()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1, 1, 1])));
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, "a.bin");
        await File.WriteAllBytesAsync(stored.Paths.ResolveMailPath(stored.EmlPath), Eml(Base64Part("a.bin", [9, 9, 9, 9, 9, 9, 9, 9])));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MailContentException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9 (A9): same size, other name.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_changed_eml_with_another_name_and_the_same_size_throws()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1, 1, 1])));
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, "a.bin");
        await File.WriteAllBytesAsync(stored.Paths.ResolveMailPath(stored.EmlPath), Eml(Base64Part("other.bin", [9, 9, 9])));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MailContentException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9 (A9): same name and size, other content type.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_changed_eml_with_another_content_type_throws()
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1, 1, 1])));
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, "a.bin");
        string changed = Base64Part("a.bin", [9, 9, 9]).Replace("application/octet-stream", "application/pdf", StringComparison.Ordinal);
        await File.WriteAllBytesAsync(stored.Paths.ResolveMailPath(stored.EmlPath), Eml(changed));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MailContentException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9 (A9, fix.1): the check also holds without any read before; an entry that does not match the stored mail fails.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("other.bin", "application/octet-stream", 3L)]
    [InlineData("a.bin", "application/pdf", 3L)]
    [InlineData("a.bin", "application/octet-stream", 4L)]
    public async Task An_unmatching_expected_entry_throws_even_without_a_read_before(string name, string contentType, long size)
    {
        Stored stored = await StoreAsync(Eml(Base64Part("a.bin", [1, 1, 1])));
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<MailContentException>(
            () => stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, 0, new MailAttachmentInfo(name, contentType, size), destination));

        Assert.Equal(0, destination.Length);
    }

    // Case 9 positive control: an EML rewritten with the same list still works.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task An_eml_rewritten_unchanged_still_delivers_the_part()
    {
        byte[] eml = Eml(Base64Part("a.bin", [4, 5, 6]));
        Stored stored = await StoreAsync(eml);
        (int index, MailAttachmentInfo info) = await ShownAsync(stored, "a.bin");
        await File.WriteAllBytesAsync(stored.Paths.ResolveMailPath(stored.EmlPath), eml);
        using var destination = new MemoryStream();

        await stored.Runtime.ReadModel.CopyAttachmentAsync(stored.Message, index, info, destination);

        Assert.Equal(new byte[] { 4, 5, 6 }, destination.ToArray());
    }
}

using MailWarden.Infrastructure.Mime;

namespace MailWarden.Infrastructure.Tests.Mime;

/// <summary>
/// AP-04c cases 10-12 (A2, A3, A8): <see cref="AttachmentFiles"/> writes real files below the test output. The
/// Zone.Identifier stream is checked for real, which needs NTFS; on another file system these tests fail with a clear
/// message instead of passing silently.
/// </summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "feature:sync")]
public sealed class AttachmentFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "ap04c-files", Guid.NewGuid().ToString("N"));

    public AttachmentFilesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a failing test may leave a handle open.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(position => (byte)(position * 31 % 256))];

    private static Func<Stream, CancellationToken, Task> Writing(byte[] content) =>
        async (stream, ct) => await stream.WriteAsync(content, ct);

    private string NewDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Fails with a clear message when the test output is not on NTFS (alternate data streams unavailable).</summary>
    private void RequireNtfs()
    {
        string drive = Path.GetPathRoot(Path.GetFullPath(_root))!;
        string format = new DriveInfo(drive).DriveFormat;
        if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"The Zone.Identifier test needs NTFS, but {drive} is {format}. Run the tests from an NTFS drive.");
        }
    }

    private static string[] AllFiles(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories) : [];

    // ---- Case 10: SaveAsync ----------------------------------------------------------------------------------

    // Case 10
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_writes_the_complete_content_and_leaves_no_temporary_file()
    {
        string directory = NewDirectory("save_ok");
        string target = Path.Combine(directory, "result_a.bin");
        byte[] content = Pattern(300_000);
        var files = new AttachmentFiles(NewDirectory("open"));

        await files.SaveAsync(Writing(content), target);

        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal(new[] { target }, AllFiles(directory));
    }

    // Case 10: the dialog has created an empty file; the content replaces it.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_replaces_the_empty_file_of_the_dialog()
    {
        string directory = NewDirectory("save_over_empty");
        string target = Path.Combine(directory, "result_b.bin");
        await File.WriteAllBytesAsync(target, []);
        var files = new AttachmentFiles(NewDirectory("open"));

        await files.SaveAsync(Writing([1, 2, 3]), target);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
        Assert.Equal(new[] { target }, AllFiles(directory));
    }

    // Case 10 / A2: the saved file carries the internet mark (zone 3).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_marks_the_target_as_from_the_internet()
    {
        RequireNtfs();
        string target = Path.Combine(NewDirectory("save_zone"), "marked_a.bin");
        var files = new AttachmentFiles(NewDirectory("open"));

        await files.SaveAsync(Writing([1, 2, 3]), target);

        string zone = await File.ReadAllTextAsync(target + ":Zone.Identifier");
        Assert.Contains("[ZoneTransfer]", zone, StringComparison.Ordinal);
        Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
    }

    // Case 10 / A2: a target that carried another zone (ZoneId=1) ends with zone 3, also when it had content.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_over_a_target_with_another_zone_ends_with_zone_3(bool targetHasContent)
    {
        RequireNtfs();
        string target = Path.Combine(NewDirectory("save_zone_over"), "marked_b.bin");
        await File.WriteAllBytesAsync(target, targetHasContent ? [7, 7, 7] : []);
        await File.WriteAllTextAsync(target + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=1\r\n");
        var files = new AttachmentFiles(NewDirectory("open"));

        await files.SaveAsync(Writing([4, 5, 6]), target);

        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(target));
        string zone = await File.ReadAllTextAsync(target + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
        Assert.DoesNotContain("ZoneId=1", zone, StringComparison.Ordinal);
    }

    // Case 10: an empty content is a valid save.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Save_of_an_empty_part_gives_an_empty_file()
    {
        string target = Path.Combine(NewDirectory("save_empty_part"), "empty_c.txt");
        var files = new AttachmentFiles(NewDirectory("open"));

        await files.SaveAsync(Writing([]), target);

        Assert.True(File.Exists(target));
        Assert.Equal(0, new FileInfo(target).Length);
    }

    // Case 10: the writer throws after writing a part; temporary file and empty target are gone.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_throwing_writer_leaves_neither_a_temporary_file_nor_the_empty_target()
    {
        string directory = NewDirectory("save_writer_throws");
        string target = Path.Combine(directory, "result_d.bin");
        await File.WriteAllBytesAsync(target, []);
        var files = new AttachmentFiles(NewDirectory("open"));

        Exception? failure = await Record.ExceptionAsync(() => files.SaveAsync(
            async (stream, ct) =>
            {
                await stream.WriteAsync(Pattern(100_000), ct);
                throw new InvalidOperationException("decoding failed");
            },
            target));

        Assert.NotNull(failure);
        Assert.Empty(AllFiles(directory));
    }

    // Case 10: the same when no file existed before (a save without the dialog's empty file).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_throwing_writer_without_a_prior_target_leaves_nothing()
    {
        string directory = NewDirectory("save_writer_throws_new");
        var files = new AttachmentFiles(NewDirectory("open"));

        Exception? failure = await Record.ExceptionAsync(() => files.SaveAsync(
            async (stream, ct) =>
            {
                await stream.WriteAsync(Pattern(10), ct);
                throw new IOException("disk full");
            },
            Path.Combine(directory, "result_e.bin")));

        Assert.NotNull(failure);
        Assert.Empty(AllFiles(directory));
    }

    // Case 10 / A8: a target that already holds data (the user confirmed overwriting) is not destroyed by a failed save.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_failed_save_keeps_an_existing_non_empty_target()
    {
        string directory = NewDirectory("save_keeps_old");
        string target = Path.Combine(directory, "result_f.bin");
        await File.WriteAllBytesAsync(target, [5, 5, 5]);
        var files = new AttachmentFiles(NewDirectory("open"));

        Exception? failure = await Record.ExceptionAsync(() => files.SaveAsync(
            (stream, _) =>
            {
                stream.WriteByte(1);
                throw new IOException("disk full");
            },
            target));

        Assert.NotNull(failure);
        Assert.Equal(new byte[] { 5, 5, 5 }, await File.ReadAllBytesAsync(target));
        Assert.Equal(new[] { target }, AllFiles(directory));
    }

    // Case 10: cancellation in the middle of the writing.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_cancelled_save_leaves_nothing()
    {
        string directory = NewDirectory("save_cancelled");
        string target = Path.Combine(directory, "result_g.bin");
        await File.WriteAllBytesAsync(target, []);
        var files = new AttachmentFiles(NewDirectory("open"));
        using var cancellation = new CancellationTokenSource();

        Exception? failure = await Record.ExceptionAsync(() => files.SaveAsync(
            async (stream, ct) =>
            {
                await stream.WriteAsync(Pattern(1000), ct);
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            },
            target,
            cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Empty(AllFiles(directory));
    }

    // Case 10: the disk write fails at the end (the target is locked, so the move over it cannot work).
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task A_target_that_cannot_be_replaced_leaves_no_temporary_file()
    {
        string directory = NewDirectory("save_locked_target");
        string target = Path.Combine(directory, "result_h.bin");
        await File.WriteAllBytesAsync(target, [7, 7]);
        var files = new AttachmentFiles(NewDirectory("open"));

        Exception? failure;
        await using (var lockHolder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failure = await Record.ExceptionAsync(() => files.SaveAsync(Writing([1, 2, 3]), target));
        }

        Assert.NotNull(failure);
        Assert.Equal(new[] { target }, AllFiles(directory));
        Assert.Equal(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(target));
    }

    // ---- Case 11: PrepareForOpeningAsync ---------------------------------------------------------------------

    // Case 11
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Prepare_writes_the_file_below_a_guid_folder_with_the_internet_mark()
    {
        RequireNtfs();
        string openFolder = NewDirectory("open_prepare");
        byte[] content = Pattern(70_000);
        var files = new AttachmentFiles(openFolder);

        string path = await files.PrepareForOpeningAsync(Writing(content), "report_a.pdf");

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal("report_a.pdf", Path.GetFileName(path));
        string guidFolder = Path.GetDirectoryName(path)!;
        Assert.True(Guid.TryParse(Path.GetFileName(guidFolder), out _), "the parent folder is not a guid");
        Assert.Equal(Path.GetFullPath(openFolder), Path.GetFullPath(Path.GetDirectoryName(guidFolder)!), ignoreCase: true);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));

        string zone = await File.ReadAllTextAsync(path + ":Zone.Identifier");
        Assert.Contains("[ZoneTransfer]", zone, StringComparison.Ordinal);
        Assert.Contains("ZoneId=3", zone, StringComparison.Ordinal);
    }

    // Case 11: every call gets its own folder, so equal names never collide.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Prepare_twice_with_the_same_name_uses_two_folders()
    {
        RequireNtfs();
        var files = new AttachmentFiles(NewDirectory("open_twice"));

        string first = await files.PrepareForOpeningAsync(Writing([1]), "same_b.txt");
        string second = await files.PrepareForOpeningAsync(Writing([2]), "same_b.txt");

        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(first));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(second));
    }

    // Case 11 / A8: a failing writer leaves no file in the open folder.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Prepare_with_a_throwing_writer_leaves_no_file()
    {
        string openFolder = NewDirectory("open_throws");
        var files = new AttachmentFiles(openFolder);

        Exception? failure = await Record.ExceptionAsync(() => files.PrepareForOpeningAsync(
            async (stream, ct) =>
            {
                await stream.WriteAsync(Pattern(500), ct);
                throw new InvalidOperationException("decoding failed");
            },
            "report_c.pdf"));

        Assert.NotNull(failure);
        Assert.Empty(AllFiles(openFolder));
    }

    // ---- Case 12: Clear --------------------------------------------------------------------------------------

    // Case 12
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_removes_all_prepared_files()
    {
        RequireNtfs();
        string openFolder = NewDirectory("open_clear");
        var files = new AttachmentFiles(openFolder);
        await files.PrepareForOpeningAsync(Writing([1]), "one_a.txt");
        await files.PrepareForOpeningAsync(Writing([2]), "two_b.txt");

        files.Clear();

        Assert.Empty(AllFiles(openFolder));
    }

    // Case 12: a locked file stays, nothing throws, the rest is gone; a second Clear finishes the job.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_with_a_locked_file_does_not_throw_and_removes_the_rest()
    {
        RequireNtfs();
        string openFolder = NewDirectory("open_clear_locked");
        var files = new AttachmentFiles(openFolder);
        string locked = await files.PrepareForOpeningAsync(Writing([1]), "locked_a.txt");
        string free = await files.PrepareForOpeningAsync(Writing([2]), "free_b.txt");

        await using (var lockHolder = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? failure = Record.Exception(files.Clear);

            Assert.Null(failure);
            Assert.False(File.Exists(free));
            Assert.True(File.Exists(locked));
        }

        files.Clear();

        Assert.Empty(AllFiles(openFolder));
    }

    // Case 12: nothing to clear (synchronous: no timeout attribute, xUnit supports it for async tests only).
    [Fact]
    public void Clear_without_an_open_folder_does_not_throw()
    {
        var files = new AttachmentFiles(Path.Combine(_root, "never_created"));

        Exception? failure = Record.Exception(files.Clear);

        Assert.Null(failure);
    }
}

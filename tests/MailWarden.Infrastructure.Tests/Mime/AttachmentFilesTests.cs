using System.Diagnostics;
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

    private readonly List<string> _links = [];
    private readonly List<string> _readOnlyFiles = [];

    public AttachmentFilesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Links first and read-only marks off, so a failing test cannot leave anything that blocks the removal of the root.
        foreach (string link in _links)
        {
            try
            {
                if (Directory.Exists(link))
                {
                    File.SetAttributes(link, File.GetAttributes(link) & ~FileAttributes.ReadOnly);
                    Directory.Delete(link, recursive: false);
                }
            }
            catch (IOException)
            {
                // Best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        foreach (string readOnlyFile in _readOnlyFiles)
        {
            if (File.Exists(readOnlyFile))
            {
                File.SetAttributes(readOnlyFile, FileAttributes.Normal);
            }
        }

        try
        {
            // Extended path: entries such as ". " or "x." are not reachable by a normalised path.
            ResetAttributes(Extended(_root));
            Directory.Delete(Extended(_root), recursive: true);
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

    private static string Extended(string normalizedPath) => @"\\?\" + normalizedPath;

    /// <summary>Removes the read-only and hidden marks below a folder so that the cleanup works; never follows a link.</summary>
    private static void ResetAttributes(string extendedFolder)
    {
        if (!Directory.Exists(extendedFolder))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(extendedFolder))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            File.SetAttributes(entry, FileAttributes.Normal);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                ResetAttributes(entry);
            }
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

    // ---- Case 12: Clear never follows a link -------------------------------------------------------------------

    /// <summary>Makes a junction with <c>mklink /J</c> (no elevation needed); fails the test when the call fails.</summary>
    private void CreateJunction(string link, string target)
    {
        CreateLink("/J", link, target);
        _links.Add(link);
    }

    /// <summary>Makes a hard link with <c>mklink /H</c> (no elevation needed on NTFS); the link is a file name, the target a file.</summary>
    private void CreateHardLink(string link, string target) => CreateLink("/H", link, target);

    private void CreateLink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add(kind);
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink {kind} failed: {output}");
    }

    /// <summary>A folder outside the open folder with a read-only file, a subfolder and a file in it.</summary>
    private (string Folder, string[] Files) NewJunctionTarget(string name)
    {
        string folder = NewDirectory(name);
        string sub = Path.Combine(folder, "sub_a");
        Directory.CreateDirectory(sub);
        string keep = Path.Combine(folder, "keep_a.txt");
        string nested = Path.Combine(sub, "keep_b.txt");
        File.WriteAllBytes(keep, [1, 2, 3]);
        File.WriteAllBytes(nested, [4, 5, 6]);
        File.SetAttributes(keep, FileAttributes.ReadOnly);
        _readOnlyFiles.Add(keep);
        return (folder, [keep, nested]);
    }

    private static void AssertTargetUntouched((string Folder, string[] Files) target, FileAttributes[] attributesBefore, FileAttributes folderAttributesBefore)
    {
        Assert.True(Directory.Exists(target.Folder), "the target folder of the link is gone");
        Assert.Equal(folderAttributesBefore, File.GetAttributes(target.Folder));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target.Files[0]));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(target.Files[1]));
        for (int position = 0; position < target.Files.Length; position++)
        {
            Assert.Equal(attributesBefore[position], File.GetAttributes(target.Files[position]));
        }

        Assert.True(File.GetAttributes(target.Files[0]).HasFlag(FileAttributes.ReadOnly), "the write protection of the target is gone");
    }

    private static bool EntryExists(string path) =>
        Directory.Exists(path) || File.Exists(path) ||
        (Directory.Exists(Path.GetDirectoryName(path)) &&
         Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!).Any(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)));

    // Case 12 / A3: a junction inside the guid folder is removed as a link; its target keeps content and write protection.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_removes_a_junction_in_a_guid_folder_and_leaves_its_target_untouched()
    {
        string openFolder = NewDirectory("open_junction_in_guid");
        var files = new AttachmentFiles(openFolder);
        string prepared = await files.PrepareForOpeningAsync(Writing([1]), "report_a.pdf");
        string guidFolder = Path.GetDirectoryName(prepared)!;
        (string Folder, string[] Files) target = NewJunctionTarget("outside_in_guid");
        FileAttributes[] before = [.. target.Files.Select(File.GetAttributes)];
        FileAttributes folderBefore = File.GetAttributes(target.Folder);
        string link = Path.Combine(guidFolder, "link_a");
        CreateJunction(link, target.Folder);
        Assert.True(File.Exists(Path.Combine(link, "keep_a.txt")), "the junction does not lead to the target");

        Exception? failure = Record.Exception(files.Clear);

        Assert.Null(failure);
        Assert.False(EntryExists(link), "the link is still there");
        Assert.False(File.Exists(prepared));
        AssertTargetUntouched(target, before, folderBefore);
    }

    // Case 12 / A3: a junction directly in the open folder.
    [Fact]
    public void Clear_removes_a_junction_directly_in_the_open_folder_and_leaves_its_target_untouched()
    {
        string openFolder = NewDirectory("open_junction_direct");
        (string Folder, string[] Files) target = NewJunctionTarget("outside_direct");
        FileAttributes[] before = [.. target.Files.Select(File.GetAttributes)];
        FileAttributes folderBefore = File.GetAttributes(target.Folder);
        string link = Path.Combine(openFolder, "link_b");
        CreateJunction(link, target.Folder);
        Assert.True(File.Exists(Path.Combine(link, "keep_a.txt")), "the junction does not lead to the target");

        Exception? failure = Record.Exception(new AttachmentFiles(openFolder).Clear);

        Assert.Null(failure);
        Assert.False(EntryExists(link), "the link is still there");
        AssertTargetUntouched(target, before, folderBefore);
    }

    // Case 12 / A3: the open folder is itself a junction; the link goes, the target and its content stay.
    [Fact]
    public void Clear_removes_the_open_folder_when_it_is_a_junction_and_leaves_its_target_untouched()
    {
        (string Folder, string[] Files) target = NewJunctionTarget("outside_open_folder");
        FileAttributes[] before = [.. target.Files.Select(File.GetAttributes)];
        FileAttributes folderBefore = File.GetAttributes(target.Folder);
        string openFolder = Path.Combine(_root, "open_is_junction");
        CreateJunction(openFolder, target.Folder);
        Assert.True(File.Exists(Path.Combine(openFolder, "keep_a.txt")), "the junction does not lead to the target");

        Exception? failure = Record.Exception(new AttachmentFiles(openFolder).Clear);

        Assert.Null(failure);
        Assert.False(EntryExists(openFolder), "the link is still there");
        AssertTargetUntouched(target, before, folderBefore);
    }

    // ---- Case 12 (fix.3): names Windows would normalise, junction attributes, hard link, open folder as a link -----------

    /// <summary>Hard limit that the test enforces itself: a hanging Clear would otherwise stop the whole run.</summary>
    private static readonly TimeSpan ClearLimit = TimeSpan.FromSeconds(20);

    private static readonly string[] TrickyNames = [". ", ".. ", "...", ".... ", ". .", ".. .", "x.", "x ", "...x ", " . "];

    public static TheoryData<string, string> TrickyNamesByKind()
    {
        var data = new TheoryData<string, string>();
        foreach (string kind in new[] { "empty folder", "folder with file", "file" })
        {
            foreach (string name in TrickyNames)
            {
                data.Add(name, kind);
            }
        }

        return data;
    }

    /// <summary>Runs Clear in its own task and fails the test when it does not end in time (it must always end).</summary>
    private static async Task ClearWithinLimitAsync(AttachmentFiles files)
    {
        Task clearing = Task.Run(files.Clear);
        try
        {
            await clearing.WaitAsync(ClearLimit);
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Clear did not end within {ClearLimit.TotalSeconds:0} s.");
        }
    }

    /// <summary>The open folder with a neighbour file and a neighbour folder beside it (what a wrong "..\" would hit).</summary>
    private (string Parent, string OpenFolder) NewOpenFolderWithNeighbours(string name)
    {
        string parent = NewDirectory(name);
        string openFolder = Path.Combine(parent, "open");
        Directory.CreateDirectory(openFolder);
        File.WriteAllBytes(Path.Combine(parent, "neighbour_a.txt"), [1, 1, 1]);
        string neighbourFolder = Path.Combine(parent, "neighbour_dir");
        Directory.CreateDirectory(neighbourFolder);
        File.WriteAllBytes(Path.Combine(neighbourFolder, "inner_a.txt"), [2, 2, 2]);
        return (parent, openFolder);
    }

    private static void AssertNeighboursIntact(string parent)
    {
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(Path.Combine(parent, "neighbour_a.txt")));
        Assert.Equal(new byte[] { 2, 2, 2 }, File.ReadAllBytes(Path.Combine(parent, "neighbour_dir", "inner_a.txt")));
    }

    /// <summary>Entries directly or deeper below a folder, read with the extended path so that no name is normalised.</summary>
    private static string[] EntriesBelow(string openFolder) =>
        Directory.Exists(Extended(openFolder))
            ? Directory.GetFileSystemEntries(Extended(openFolder), "*", SearchOption.AllDirectories)
            : [];

    private static void Create(string parentFolder, string name, string kind, byte marker)
    {
        string path = Extended(parentFolder) + "\\" + name;
        if (kind == "file")
        {
            File.WriteAllBytes(path, [marker]);
            return;
        }

        Directory.CreateDirectory(path);
        if (kind == "folder with file")
        {
            File.WriteAllBytes(path + "\\inner_b.txt", [marker]);
        }
    }

    // Case 12 (fix.3): every name alone, as an empty folder, a folder with a file and a file.
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(TrickyNamesByKind))]
    public async Task Clear_ends_and_removes_an_entry_with_a_name_windows_would_normalise(string name, string kind)
    {
        (string parent, string openFolder) = NewOpenFolderWithNeighbours("tricky_single");
        Create(openFolder, name, kind, 7);
        Assert.Single(Directory.GetFileSystemEntries(Extended(openFolder)));
        var files = new AttachmentFiles(openFolder);

        await ClearWithinLimitAsync(files);

        Assert.Empty(EntriesBelow(openFolder));
        AssertNeighboursIntact(parent);
    }

    // Case 12 (fix.3): all names together, as folders with content, as files and as empty folders, beside a regular prepared file.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_ends_and_removes_all_tricky_names_together()
    {
        RequireNtfs();
        (string parent, string openFolder) = NewOpenFolderWithNeighbours("tricky_together");
        var files = new AttachmentFiles(openFolder);
        string prepared = await files.PrepareForOpeningAsync(Writing([3]), "report_a.pdf");
        string guidFolder = Path.GetDirectoryName(prepared)!;
        string filesFolder = Path.Combine(guidFolder, "as_files");
        string emptyFolder = Path.Combine(guidFolder, "as_empty");
        Directory.CreateDirectory(filesFolder);
        Directory.CreateDirectory(emptyFolder);
        foreach (string name in TrickyNames)
        {
            Create(openFolder, name, "folder with file", 1);
            Create(filesFolder, name, "file", 2);
            Create(emptyFolder, name, "empty folder", 3);
        }

        await ClearWithinLimitAsync(files);

        Assert.Empty(EntriesBelow(openFolder));
        AssertNeighboursIntact(parent);
    }

    // Case 12 (fix.3): the names nested in each other, a file at every level.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_ends_and_removes_tricky_names_nested_in_each_other()
    {
        (string parent, string openFolder) = NewOpenFolderWithNeighbours("tricky_nested");
        string chain = Extended(openFolder);
        foreach (string name in TrickyNames)
        {
            chain += "\\" + name;
            Directory.CreateDirectory(chain);
            File.WriteAllBytes(chain + "\\level_file.txt", [9]);
            File.WriteAllBytes(chain + "\\" + name.TrimEnd() + "_f.txt", [8]);
        }

        // A second chain in reverse order, so that each name also appears below each other name.
        string reverse = Extended(openFolder) + "\\reverse_a";
        foreach (string name in TrickyNames.Reverse())
        {
            reverse += "\\" + name;
            Directory.CreateDirectory(reverse);
            File.WriteAllBytes(reverse + "\\level_file.txt", [9]);
        }

        Assert.NotEmpty(EntriesBelow(openFolder));

        await ClearWithinLimitAsync(new AttachmentFiles(openFolder));

        Assert.Empty(EntriesBelow(openFolder));
        AssertNeighboursIntact(parent);
    }

    // Case 12 (fix.3) / A3: a junction with the read-only attribute is removed; the target and its attributes stay.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_removes_a_read_only_junction_and_leaves_its_target_and_attributes_untouched()
    {
        string openFolder = NewDirectory("open_junction_readonly");
        (string Folder, string[] Files) target = NewJunctionTarget("outside_readonly_junction");
        FileAttributes[] before = [.. target.Files.Select(File.GetAttributes)];
        FileAttributes folderBefore = File.GetAttributes(target.Folder);
        string link = Path.Combine(openFolder, "link_ro");
        CreateJunction(link, target.Folder);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.ReadOnly);
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReadOnly), "setup: the link did not take ReadOnly");
        Assert.Equal(folderBefore, File.GetAttributes(target.Folder));
        var files = new AttachmentFiles(openFolder);

        await ClearWithinLimitAsync(files);

        Assert.False(EntryExists(link), "the read-only link is still there");
        AssertTargetUntouched(target, before, folderBefore);
    }

    // Case 12 (fix.3): a regular folder with the read-only attribute and a write-protected file in it is removed.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_removes_a_read_only_folder_with_a_write_protected_file()
    {
        (string parent, string openFolder) = NewOpenFolderWithNeighbours("open_readonly_folder");
        string folder = Path.Combine(openFolder, "protected_a");
        string nestedFolder = Path.Combine(folder, "protected_sub");
        Directory.CreateDirectory(nestedFolder);
        string file = Path.Combine(folder, "locked_a.txt");
        string nestedFile = Path.Combine(nestedFolder, "locked_b.txt");
        File.WriteAllBytes(file, [1]);
        File.WriteAllBytes(nestedFile, [2]);
        foreach (string path in new[] { file, nestedFile })
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            _readOnlyFiles.Add(path);
        }

        File.SetAttributes(nestedFolder, FileAttributes.Directory | FileAttributes.ReadOnly);
        File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.ReadOnly);

        await ClearWithinLimitAsync(new AttachmentFiles(openFolder));

        Assert.Empty(EntriesBelow(openFolder));
        AssertNeighboursIntact(parent);
    }

    // Case 12 (fix.3): a hard link is removed; the file outside keeps its content and its Hidden attribute.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Clear_removes_a_hard_link_and_leaves_content_and_hidden_attribute_of_the_file_outside()
    {
        string openFolder = NewDirectory("open_hardlink");
        string outside = Path.Combine(NewDirectory("outside_hardlink"), "outside_a.txt");
        File.WriteAllBytes(outside, [4, 5, 6]);
        File.SetAttributes(outside, FileAttributes.Hidden);
        _readOnlyFiles.Add(outside);
        string link = Path.Combine(openFolder, "hard_a.txt");
        CreateHardLink(link, outside);
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(link));
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.Hidden), "setup: the hard link does not share Hidden");
        var files = new AttachmentFiles(openFolder);

        await ClearWithinLimitAsync(files);

        Assert.False(File.Exists(link), "the hard link is still there");
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(outside));
        Assert.True(File.GetAttributes(outside).HasFlag(FileAttributes.Hidden), "Hidden of the file outside is gone");
    }

    // ---- Case 11 (fix.3): the open folder is itself a link -----------------------------------------------------

    // Case 11 / A3: PrepareForOpeningAsync refuses when the open folder is a junction; nothing in the target; after Clear it works.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Prepare_refuses_an_open_folder_that_is_a_junction_and_works_again_after_clear()
    {
        RequireNtfs();
        string linkTarget = NewDirectory("outside_prepare_target");
        string openFolder = Path.Combine(_root, "open_prepare_junction");
        CreateJunction(openFolder, linkTarget);
        var files = new AttachmentFiles(openFolder);

        await Assert.ThrowsAnyAsync<IOException>(() => files.PrepareForOpeningAsync(Writing([1, 2, 3]), "report_j.pdf"));

        Assert.Empty(Directory.GetFileSystemEntries(linkTarget, "*", SearchOption.AllDirectories));

        await ClearWithinLimitAsync(files);
        string path = await files.PrepareForOpeningAsync(Writing([4, 5]), "report_k.pdf");

        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFileSystemEntries(linkTarget, "*", SearchOption.AllDirectories));
        Assert.True(Directory.Exists(linkTarget));
    }

    // fix.4 S3: the refusal for a junction as the open folder carries its own data key, so the UI can name the restart.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Prepare_refusal_for_a_junction_as_open_folder_carries_the_link_data_key()
    {
        RequireNtfs();
        string linkTarget = NewDirectory("outside_key_target");
        string openFolder = Path.Combine(_root, "open_key_junction");
        CreateJunction(openFolder, linkTarget);
        var files = new AttachmentFiles(openFolder);

        IOException failure = await Assert.ThrowsAnyAsync<IOException>(
            () => files.PrepareForOpeningAsync(Writing([1, 2, 3]), "report_l.pdf"));

        Assert.True(failure.Data.Contains(AttachmentFiles.OpenFolderIsLinkDataKey), "the data key is missing");
        Assert.Equal(true, failure.Data[AttachmentFiles.OpenFolderIsLinkDataKey]);
        Assert.Empty(Directory.GetFileSystemEntries(linkTarget, "*", SearchOption.AllDirectories));
    }

    // ---- Discard (fix.3) -----------------------------------------------------------------------------------------

    // Contract: Discard removes a prepared file that will not be opened, with its guid folder.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discard_removes_the_prepared_file_and_its_folder_and_leaves_others()
    {
        RequireNtfs();
        string openFolder = NewDirectory("open_discard");
        var files = new AttachmentFiles(openFolder);
        string discarded = await files.PrepareForOpeningAsync(Writing([1]), "gone_a.pdf");
        string kept = await files.PrepareForOpeningAsync(Writing([2]), "kept_b.pdf");

        files.Discard(discarded);

        Assert.False(File.Exists(discarded));
        Assert.False(Directory.Exists(Path.GetDirectoryName(discarded)!));
        Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(kept));
        Assert.True(Directory.Exists(openFolder));
    }

    // Contract: Discard of a file that is not there, or of an empty open folder, is no error.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discard_of_a_missing_file_does_not_throw()
    {
        string openFolder = NewDirectory("open_discard_missing");
        var files = new AttachmentFiles(openFolder);
        string path = await files.PrepareForOpeningAsync(Writing([1]), "gone_c.pdf");
        files.Discard(path);

        Exception? again = Record.Exception(() => files.Discard(path));
        Exception? never = Record.Exception(() => files.Discard(Path.Combine(openFolder, Guid.NewGuid().ToString("D"), "never_d.pdf")));

        Assert.Null(again);
        Assert.Null(never);
    }

    // Contract: Discard of a locked file does not throw; Clear finishes the job later.
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Discard_of_a_locked_file_does_not_throw_and_clear_finishes_it()
    {
        string openFolder = NewDirectory("open_discard_locked");
        var files = new AttachmentFiles(openFolder);
        string path = await files.PrepareForOpeningAsync(Writing([1]), "locked_e.pdf");

        await using (var lockHolder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? failure = Record.Exception(() => files.Discard(path));
            Assert.Null(failure);
        }

        await ClearWithinLimitAsync(files);

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

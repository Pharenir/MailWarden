namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class FileEmlStoreTests
{
    private static byte[] SampleBytes(int length, int seed) =>
        Enumerable.Range(0, length).Select(index => (byte)((index * 7) + seed)).ToArray();

    private static async Task<byte[]> ReadAllAsync(IEmlStore store, string relativePath)
    {
        await using Stream stream = await store.OpenReadAsync(relativePath);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // Case 24
    [Fact]
    public void Relative_path_uses_lower_case_guid_and_forward_slashes()
    {
        using var temp = new TempDirectory();
        var store = new FileEmlStore(new DataPaths(temp.RootPath));
        Guid accountId = Guid.NewGuid();

        string relativePath = store.RelativePathFor(accountId, 7, 42);

        Assert.Equal($"accounts/{accountId.ToString("D").ToLowerInvariant()}/folders/7/42.eml", relativePath);
        Assert.DoesNotContain('\\', relativePath);
    }

    // Case 25
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Write_creates_directories_and_content_is_read_back_identically()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.RootPath);
        var store = new FileEmlStore(paths);
        string relativePath = store.RelativePathFor(Guid.NewGuid(), 3, 11);
        byte[] content = SampleBytes(4096, 1);

        await store.WriteAsync(relativePath, new MemoryStream(content));

        Assert.True(store.Exists(relativePath));
        Assert.True(File.Exists(paths.ResolveMailPath(relativePath)));
        Assert.Equal(content, await ReadAllAsync(store, relativePath));
    }

    // Case 25
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_write_overwrites_with_shorter_content_and_leaves_no_tmp_file()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.RootPath);
        var store = new FileEmlStore(paths);
        string relativePath = store.RelativePathFor(Guid.NewGuid(), 1, 1);
        byte[] longContent = SampleBytes(2048, 3);
        byte[] shortContent = SampleBytes(100, 9);

        await store.WriteAsync(relativePath, new MemoryStream(longContent));
        await store.WriteAsync(relativePath, new MemoryStream(shortContent));

        Assert.Equal(shortContent, await ReadAllAsync(store, relativePath));
        string[] files = Directory.GetFiles(paths.MailRoot, "*", SearchOption.AllDirectories);
        Assert.DoesNotContain(files, file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Single(files);
    }

    // Case 25 (supporting): empty content is a valid file
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Empty_content_round_trips()
    {
        using var temp = new TempDirectory();
        var store = new FileEmlStore(new DataPaths(temp.RootPath));
        string relativePath = store.RelativePathFor(Guid.NewGuid(), 1, 2);

        await store.WriteAsync(relativePath, new MemoryStream());

        Assert.True(store.Exists(relativePath));
        Assert.Empty(await ReadAllAsync(store, relativePath));
    }

    // Case 26
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Open_read_of_missing_path_throws_EmlNotFoundException_with_relative_path()
    {
        using var temp = new TempDirectory();
        var store = new FileEmlStore(new DataPaths(temp.RootPath));
        string relativePath = store.RelativePathFor(Guid.NewGuid(), 9, 99);

        EmlNotFoundException exception =
            await Assert.ThrowsAsync<EmlNotFoundException>(() => store.OpenReadAsync(relativePath));

        Assert.Equal(relativePath, exception.RelativePath);
        Assert.Contains(relativePath, exception.Message);
        Assert.IsAssignableFrom<StorageException>(exception);
    }

    // Case 26
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Exists_is_false_and_delete_is_a_no_op_for_missing_path()
    {
        using var temp = new TempDirectory();
        var store = new FileEmlStore(new DataPaths(temp.RootPath));
        string relativePath = store.RelativePathFor(Guid.NewGuid(), 9, 99);

        Assert.False(store.Exists(relativePath));
        await store.DeleteAsync(relativePath);

        Assert.False(store.Exists(relativePath));
    }

    // Contract: delete removes an existing file
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_removes_existing_file()
    {
        using var temp = new TempDirectory();
        var store = new FileEmlStore(new DataPaths(temp.RootPath));
        string relativePath = store.RelativePathFor(Guid.NewGuid(), 1, 5);
        await store.WriteAsync(relativePath, new MemoryStream(SampleBytes(10, 0)));

        await store.DeleteAsync(relativePath);

        Assert.False(store.Exists(relativePath));
    }
}

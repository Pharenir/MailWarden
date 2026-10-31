using System.Text;
using MailWarden.Core.Accounts;
using MailWarden.Infrastructure.Security;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class DpapiCredentialStoreTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("plain-secret-value-4711-artificial");

    public static TheoryData<string?> InvalidKeys => new()
    {
        "../x", "a/b", "a:b", "", new string('k', 129), null,
    };

    // Case 1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Write_then_read_returns_identical_bytes_and_file_does_not_contain_plain_secret()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);

        await store.WriteAsync("key.one", Secret);

        byte[]? read = await store.ReadAsync("key.one");
        Assert.Equal(Secret, read);
        string filePath = Path.Combine(temp.RootPath, "key.one.bin");
        Assert.True(File.Exists(filePath));
        byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
        Assert.NotEmpty(fileBytes);
        Assert.False(fileBytes.AsSpan().IndexOf(Secret) >= 0, "File contains the plain secret bytes.");
        Assert.DoesNotContain("plain-secret", Encoding.UTF8.GetString(fileBytes), StringComparison.Ordinal);
    }

    // Case 2
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Overwrite_returns_new_value_and_leaves_no_tmp_file()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);
        byte[] newSecret = Encoding.UTF8.GetBytes("second-value");

        await store.WriteAsync("k", Secret);
        await store.WriteAsync("k", newSecret);

        Assert.Equal(newSecret, await store.ReadAsync("k"));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp", SearchOption.AllDirectories));
    }

    // Case 3
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Read_of_missing_key_returns_null_and_delete_is_noop()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);

        Assert.Null(await store.ReadAsync("missing"));
        await store.DeleteAsync("missing");
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Delete_removes_existing_key()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);
        await store.WriteAsync("k", Secret);

        await store.DeleteAsync("k");

        Assert.Null(await store.ReadAsync("k"));
    }

    // Case 4
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [MemberData(nameof(InvalidKeys))]
    public async Task Invalid_keys_are_rejected_on_every_method(string? key)
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);

        if (key is null)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReadAsync(key!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => store.WriteAsync(key!, Secret));
            await Assert.ThrowsAsync<ArgumentNullException>(() => store.DeleteAsync(key!));
        }
        else
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReadAsync(key));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteAsync(key, Secret));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteAsync(key));
        }
    }

    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Key_of_128_valid_characters_is_accepted()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);
        string key = new('a', 128);

        await store.WriteAsync(key, Secret);

        Assert.Equal(Secret, await store.ReadAsync(key));
    }

    // Case 5
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListKeys_returns_prefix_matches_sorted()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);
        await store.WriteAsync("msal.cache", Secret);
        await store.WriteAsync("google.b", Secret);
        await store.WriteAsync("google.a", Secret);

        IReadOnlyList<string> keys = await store.ListKeysAsync("google.");

        Assert.Equal(["google.a", "google.b"], keys);
    }

    // A1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListKeys_with_empty_prefix_returns_all_keys_sorted()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);
        await store.WriteAsync("msal.cache", Secret);
        await store.WriteAsync("google.a", Secret);

        IReadOnlyList<string> keys = await store.ListKeysAsync(string.Empty);

        Assert.Equal(["google.a", "msal.cache"], keys);
    }

    // A1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListKeys_with_null_prefix_throws_ArgumentNull()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ListKeysAsync(null!));
    }

    // A1
    [Theory(Timeout = TestTimeouts.PerTestMilliseconds)]
    [InlineData("../")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    public async Task ListKeys_with_invalid_prefix_throws_ArgumentException(string prefix)
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListKeysAsync(prefix));
    }

    // A1
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task ListKeys_with_prefix_longer_than_128_chars_throws_ArgumentException()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListKeysAsync(new string('a', 129)));
    }

    // Case 6
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Corrupt_file_throws_CredentialStoreException_without_bytes_in_message()
    {
        using var temp = new TempDirectory();
        var store = new DpapiCredentialStore(temp.RootPath);
        await store.WriteAsync("k", Secret);
        byte[] garbage = new byte[64];
        new Random(42).NextBytes(garbage);
        await File.WriteAllBytesAsync(Path.Combine(temp.RootPath, "k.bin"), garbage);

        CredentialStoreException exception = await Assert.ThrowsAsync<CredentialStoreException>(() => store.ReadAsync("k"));

        Assert.DoesNotContain(Convert.ToHexString(garbage), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(garbage), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", exception.Message, StringComparison.Ordinal);
    }

    // Case 7
    [Fact(Timeout = TestTimeouts.PerTestMilliseconds)]
    public async Task Second_instance_on_same_directory_reads_what_first_wrote()
    {
        using var temp = new TempDirectory();
        var first = new DpapiCredentialStore(temp.RootPath);
        var second = new DpapiCredentialStore(temp.RootPath);

        await first.WriteAsync("shared", Secret);

        Assert.Equal(Secret, await second.ReadAsync("shared"));
    }
}

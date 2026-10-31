namespace MailWarden.Infrastructure.Tests.Storage;

[Trait("Category", "feature:storage")]
public sealed class DataPathsTests
{
    // Pure, synchronous path logic: no async work, so no per-test timeout needed (xUnit v2 cannot interrupt sync code).
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "mailwarden-tests", Guid.NewGuid().ToString("N"));

    // Case 28
    [Fact]
    public void Properties_are_derived_from_the_root()
    {
        string root = NewRoot();

        var paths = new DataPaths(root);

        Assert.Equal(root, paths.Root);
        Assert.Equal(Path.Combine(root, "mailwarden.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(root, "mail"), paths.MailRoot);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogRoot);
    }

    // Case 27
    [Fact]
    public void Resolve_rejects_parent_segment_at_start()
    {
        var paths = new DataPaths(NewRoot());

        Assert.ThrowsAny<ArgumentException>(() => paths.ResolveMailPath("../x.eml"));
    }

    // Case 27
    [Fact]
    public void Resolve_rejects_rooted_path()
    {
        var paths = new DataPaths(NewRoot());

        Assert.ThrowsAny<ArgumentException>(() => paths.ResolveMailPath("C:\\x.eml"));
    }

    // Case 27
    [Fact]
    public void Resolve_rejects_parent_segment_in_the_middle()
    {
        var paths = new DataPaths(NewRoot());

        Assert.ThrowsAny<ArgumentException>(() => paths.ResolveMailPath("accounts/../../x"));
    }

    // Case 27 (D5): trailing dot/space segments and DOS device names
    [Theory]
    [InlineData("x.eml.")]
    [InlineData("x.eml ")]
    [InlineData("...")]
    [InlineData("accounts/...")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9.eml")]
    [InlineData("accounts/a/aux/1.eml")]
    public void Resolve_rejects_trailing_dot_space_and_device_names(string relativePath)
    {
        var paths = new DataPaths(NewRoot());

        Assert.ThrowsAny<ArgumentException>(() => paths.ResolveMailPath(relativePath));
    }

    // Case 15a (D7)
    [Fact]
    public void Constructor_rejects_null_root_with_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => { _ = new DataPaths(null!); });
    }

    // Case 15a (D7)
    [Fact]
    public void Constructor_rejects_relative_root_with_ArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => { _ = new DataPaths("relative_root_dir"); });
    }

    // Case 27
    [Fact]
    public void Resolve_maps_relative_path_below_mail_root_with_os_separators()
    {
        var paths = new DataPaths(NewRoot());

        string resolved = paths.ResolveMailPath("accounts/a/folders/1/2.eml");

        Assert.Equal(
            Path.Combine(paths.MailRoot, "accounts", "a", "folders", "1", "2.eml"),
            resolved);
        Assert.StartsWith(paths.MailRoot + Path.DirectorySeparatorChar, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('/', resolved);
    }
}

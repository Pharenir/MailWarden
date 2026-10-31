using MailWarden.Core.Accounts;
using MailWarden.Infrastructure.Auth;

namespace MailWarden.Infrastructure.Tests.Accounts;

[Trait("Category", "feature:accounts")]
public sealed class AuthConfigurationTests
{
    private const string FileJson =
        """{"microsoftClientId": "file-ms", "googleClientId": "file-g", "googleClientSecret": "file-secret"}""";

    private static Func<string, string?> Env(Dictionary<string, string> values) =>
        name => values.TryGetValue(name, out string? value) ? value : null;

    private static string WriteConfig(TempDirectory temp, string content)
    {
        string path = Path.Combine(temp.RootPath, "auth-config.json");
        File.WriteAllText(path, content);
        return path;
    }

    // Case 18
    [Fact]
    public void Load_reads_values_from_file_only()
    {
        using var temp = new TempDirectory();

        AuthConfiguration config = AuthConfiguration.Load(WriteConfig(temp, FileJson), Env([]));

        Assert.Equal("file-ms", config.MicrosoftClientId);
        Assert.Equal("file-g", config.GoogleClientId);
        Assert.Equal("file-secret", config.GoogleClientSecret);
    }

    [Fact]
    public void Load_reads_values_from_environment_only()
    {
        using var temp = new TempDirectory();
        var env = new Dictionary<string, string>
        {
            ["MAILWARDEN_MS_CLIENT_ID"] = "env-ms",
            ["MAILWARDEN_GOOGLE_CLIENT_ID"] = "env-g",
            ["MAILWARDEN_GOOGLE_CLIENT_SECRET"] = "env-secret",
        };

        AuthConfiguration config = AuthConfiguration.Load(Path.Combine(temp.RootPath, "absent.json"), Env(env));

        Assert.Equal(new AuthConfiguration("env-ms", "env-g", "env-secret"), config);
    }

    [Fact]
    public void Environment_overrides_file_per_value()
    {
        using var temp = new TempDirectory();
        var env = new Dictionary<string, string> { ["MAILWARDEN_MS_CLIENT_ID"] = "env-ms" };

        AuthConfiguration config = AuthConfiguration.Load(WriteConfig(temp, FileJson), Env(env));

        Assert.Equal("env-ms", config.MicrosoftClientId);
        Assert.Equal("file-g", config.GoogleClientId);
        Assert.Equal("file-secret", config.GoogleClientSecret);
    }

    [Fact]
    public void Missing_file_and_no_environment_yield_all_null()
    {
        using var temp = new TempDirectory();

        AuthConfiguration config = AuthConfiguration.Load(Path.Combine(temp.RootPath, "absent.json"), Env([]));

        Assert.Equal(new AuthConfiguration(null, null, null), config);
    }

    [Fact]
    public void RequireMicrosoftClientId_without_value_throws_Configuration_naming_the_key()
    {
        var config = new AuthConfiguration(null, null, null);

        AuthenticationFailedException exception = Assert.Throws<AuthenticationFailedException>(config.RequireMicrosoftClientId);

        Assert.Equal(AuthFailureReason.Configuration, exception.Reason);
        Assert.Contains("MAILWARDEN_MS_CLIENT_ID", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireGoogleClient_without_secret_throws_Configuration_naming_the_key()
    {
        var config = new AuthConfiguration(null, "g-id", null);

        AuthenticationFailedException exception = Assert.Throws<AuthenticationFailedException>(() => config.RequireGoogleClient());

        Assert.Equal(AuthFailureReason.Configuration, exception.Reason);
        Assert.Contains("MAILWARDEN_GOOGLE_CLIENT_SECRET", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Require_methods_return_values_when_present()
    {
        var config = new AuthConfiguration("ms-id", "g-id", "g-secret");

        Assert.Equal("ms-id", config.RequireMicrosoftClientId());
        Assert.Equal(("g-id", "g-secret"), config.RequireGoogleClient());
    }

    [Fact]
    public void Malformed_json_throws_Configuration()
    {
        using var temp = new TempDirectory();
        string path = WriteConfig(temp, "{ this is not json");

        AuthenticationFailedException exception =
            Assert.Throws<AuthenticationFailedException>(() => AuthConfiguration.Load(path, Env([])));

        Assert.Equal(AuthFailureReason.Configuration, exception.Reason);
    }
}

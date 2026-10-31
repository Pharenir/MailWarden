using MailWarden.Core.Accounts;

namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>
/// Hand-written <see cref="ITokenProvider"/> fake. Interactive sign-in returns the next queued username (or throws
/// the configured exception); every sign-out is recorded.
/// </summary>
internal sealed class FakeTokenProvider(MailProvider provider = MailProvider.Microsoft) : ITokenProvider
{
    private readonly Queue<string> signInUsernames = new();
    private string lastUsername = "user_a@example.test";

    public MailProvider Provider { get; } = provider;

    /// <summary>Exception thrown by interactive sign-in when set.</summary>
    public Exception? InteractiveException { get; set; }

    /// <summary>Exception thrown by silent token acquisition when set.</summary>
    public Exception? SilentException { get; set; }

    /// <summary>Usernames passed to <see cref="SignOutAsync"/>, in call order.</summary>
    public List<string> SignedOutUsernames { get; } = [];

    /// <summary>Login hints passed to <see cref="SignInInteractiveAsync"/>, in call order.</summary>
    public List<string?> LoginHints { get; } = [];

    /// <summary>Queues the username the next interactive sign-in returns (the last one repeats).</summary>
    public void EnqueueSignIn(string username) => signInUsernames.Enqueue(username);

    public Task<AccessToken> GetAccessTokenSilentAsync(string username, CancellationToken ct = default)
    {
        if (SilentException is not null)
        {
            throw SilentException;
        }

        return Task.FromResult(new AccessToken("fake-token-for-" + username, DateTimeOffset.UtcNow.AddHours(1), username));
    }

    public Task<AccessToken> SignInInteractiveAsync(string? loginHint, CancellationToken ct = default)
    {
        LoginHints.Add(loginHint);
        if (InteractiveException is not null)
        {
            throw InteractiveException;
        }

        if (signInUsernames.Count > 0)
        {
            lastUsername = signInUsernames.Dequeue();
        }

        return Task.FromResult(new AccessToken("fake-token-for-" + lastUsername, DateTimeOffset.UtcNow.AddHours(1), lastUsername));
    }

    public Task SignOutAsync(string username, CancellationToken ct = default)
    {
        SignedOutUsernames.Add(username);
        return Task.CompletedTask;
    }
}

using MailWarden.Core.Accounts;
using MailWarden.Core.Model;

namespace MailWarden.Infrastructure.Auth;

/// <summary>
/// Upper time limit for interactive (browser) sign-ins. A browser page that never redirects back (closed tab, Google
/// error page of an unpublished app) would otherwise keep the loopback listener waiting until the caller cancels.
/// </summary>
internal static class InteractiveSignInTimeout
{
    /// <summary>Timeout used when the token provider is created without an explicit value.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    // CancellationTokenSource.CancelAfter accepts at most this many milliseconds on every supported runtime.
    private static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Returns the effective timeout for an optional constructor argument.</summary>
    /// <param name="timeout">Requested timeout, or null for <see cref="Default"/>.</param>
    /// <param name="parameterName">Constructor parameter name for the exception.</param>
    /// <returns>The timeout to use.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive or exceeds about 24.8 days.</exception>
    public static TimeSpan Resolve(TimeSpan? timeout, string parameterName)
    {
        TimeSpan effectiveTimeout = timeout ?? Default;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                effectiveTimeout,
                "The interactive sign-in timeout must be positive and at most about 24.8 days.");
        }

        return effectiveTimeout;
    }

    /// <summary>
    /// Runs an interactive sign-in with a time limit. The operation receives a token that is cancelled by the caller
    /// or by the timeout. Caller cancellation surfaces as <see cref="OperationCanceledException"/>; the timeout as
    /// <see cref="AuthenticationFailedException"/> with code <see cref="AuthFailures.SignInTimeoutCode"/>.
    /// </summary>
    /// <typeparam name="TResult">Result of the sign-in.</typeparam>
    /// <param name="provider">Provider that signs in (for the error).</param>
    /// <param name="timeout">Time limit.</param>
    /// <param name="operation">The sign-in; must observe the token it receives.</param>
    /// <param name="ct">Caller's cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    public static async Task<TResult> RunAsync<TResult>(
        MailProvider provider,
        TimeSpan timeout,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        using var signInSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        signInSource.CancelAfter(timeout);
        try
        {
            return await operation(signInSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not AuthenticationFailedException && signInSource.IsCancellationRequested)
        {
            // Whatever the aborted library threw (cancellation, listener stopped): caller first, then timeout.
            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("The sign-in was cancelled.", exception, ct);
            }

            throw AuthFailures.SignInTimeout(provider, exception);
        }
    }
}

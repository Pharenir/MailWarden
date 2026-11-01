namespace MailWarden.Presentation;

/// <summary>
/// What the reading pane does with a navigation.
/// </summary>
public enum NavigationDecision
{
    /// <summary>The navigation may run: it is the message document itself.</summary>
    Allow = 1,

    /// <summary>The navigation is cancelled and nothing else happens.</summary>
    Block = 2,

    /// <summary>The navigation is cancelled and the system handler opens the URI.</summary>
    OpenExternally = 3,
}

/// <summary>
/// Decisions for the reading pane's browser control. Pure, so the rules can be tested without a browser; the wiring lives
/// in the app.
/// </summary>
public static class WebContentPolicy
{
    /// <summary>Address of the message document. It comes from memory; the reserved domain never resolves.</summary>
    public const string DocumentUri = "https://mail.mailwarden.invalid/message.html";

    /// <summary>Decides about a navigation.</summary>
    /// <param name="uri">Target of the navigation.</param>
    /// <returns>
    /// <see cref="DocumentUri"/> (exact) → Allow. Absolute http/https/mailto without user info → OpenExternally.
    /// Everything else (other schemes, relative, unparseable, http/https with user info such as
    /// <c>https://bank.test@evil.test</c>) → Block.
    /// </returns>
    public static NavigationDecision DecideNavigation(string uri)
    {
        if (uri == DocumentUri)
        {
            return NavigationDecision.Allow;
        }

        if (!TryParseAbsolute(uri, out Uri? parsed))
        {
            return NavigationDecision.Block;
        }

        if (parsed.Scheme == Uri.UriSchemeMailto)
        {
            return NavigationDecision.OpenExternally;
        }

        return IsWebScheme(parsed) && parsed.UserInfo.Length == 0 && parsed.Host.Length > 0
            ? NavigationDecision.OpenExternally
            : NavigationDecision.Block;
    }

    /// <summary>Decides whether the browser may load a resource.</summary>
    /// <param name="uri">Address of the resource.</param>
    /// <param name="isImageRequest">True when the browser requests it as an image.</param>
    /// <param name="allowRemoteImages">True when the user approved remote images for this message.</param>
    /// <returns>
    /// <see cref="DocumentUri"/> → true. http/https: only when <paramref name="isImageRequest"/> and
    /// <paramref name="allowRemoteImages"/>. Everything else false.
    /// </returns>
    public static bool AllowRequest(string uri, bool isImageRequest, bool allowRemoteImages)
    {
        if (uri == DocumentUri)
        {
            return true;
        }

        return isImageRequest && allowRemoteImages && TryParseAbsolute(uri, out Uri? parsed) && IsWebScheme(parsed);
    }

    private static bool IsWebScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    // Uri trims and tolerates whitespace and control characters, a browser or handler may read them differently.
    private static bool TryParseAbsolute(string? uri, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? parsed)
    {
        parsed = null;
        return !string.IsNullOrEmpty(uri)
            && !uri.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            && Uri.TryCreate(uri, UriKind.Absolute, out parsed);
    }
}

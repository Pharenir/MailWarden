using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;

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

    private const string MailtoPrefix = "mailto:";
    private const string LocalhostName = "localhost";
    private const string LocalhostSuffix = ".localhost";

    private static readonly char[] MailtoParameterSeparators = ['&', ';'];

    private static readonly FrozenSet<string> MailtoParameters =
        new[] { "to", "cc", "bcc", "subject", "body" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Decides about a navigation and its origin: without a user gesture nothing opens outside the app, so that a mail
    /// cannot start a browser by itself even if something gets past the sanitizer.
    /// </summary>
    /// <param name="uri">Target of the navigation.</param>
    /// <param name="isUserInitiated">True when the browser reports a user gesture.</param>
    /// <returns>As <see cref="DecideNavigation(string)"/>, but OpenExternally becomes Block when not user initiated.</returns>
    public static NavigationDecision DecideNavigation(string uri, bool isUserInitiated)
    {
        NavigationDecision decision = DecideNavigation(uri);
        return decision == NavigationDecision.OpenExternally && !isUserInitiated ? NavigationDecision.Block : decision;
    }

    /// <summary>
    /// Keeps only the parameters <c>to</c>, <c>cc</c>, <c>bcc</c>, <c>subject</c> and <c>body</c> of a <c>mailto:</c> URI
    /// (names compared case-insensitively, values unchanged), so that a link cannot make the mail program attach a file.
    /// </summary>
    /// <param name="uri">URI that is about to be opened.</param>
    /// <returns>The filtered URI; any other URI than <c>mailto:</c> unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    public static string SanitizeMailto(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.StartsWith(MailtoPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        // A fragment means nothing in mailto and is not passed on.
        int fragmentStart = uri.IndexOf('#', StringComparison.Ordinal);
        string withoutFragment = fragmentStart < 0 ? uri : uri[..fragmentStart];
        int queryStart = withoutFragment.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return withoutFragment;
        }

        IEnumerable<string> keptParameters = withoutFragment[(queryStart + 1)..]
            .Split(MailtoParameterSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(IsAllowedMailtoParameter);
        string query = string.Join('&', keptParameters);
        return query.Length == 0 ? withoutFragment[..queryStart] : $"{withoutFragment[..queryStart]}?{query}";
    }

    /// <summary>Decides whether the browser may load a resource.</summary>
    /// <param name="uri">Address of the resource.</param>
    /// <param name="isImageRequest">True when the browser requests it as an image.</param>
    /// <param name="allowRemoteImages">True when the user approved remote images for this message.</param>
    /// <returns>
    /// <see cref="DocumentUri"/> → true. http/https: only when <paramref name="isImageRequest"/> and
    /// <paramref name="allowRemoteImages"/> and the host is not <c>localhost</c>, loopback or a private or link-local IP
    /// literal (an image must not reach into the local network). Everything else false.
    /// </returns>
    public static bool AllowRequest(string uri, bool isImageRequest, bool allowRemoteImages)
    {
        if (uri == DocumentUri)
        {
            return true;
        }

        return isImageRequest
            && allowRemoteImages
            && TryParseAbsolute(uri, out Uri? parsed)
            && IsWebScheme(parsed)
            && !IsLocalNetworkHost(parsed);
    }

    private static bool IsAllowedMailtoParameter(string parameter)
    {
        int separator = parameter.IndexOf('=', StringComparison.Ordinal);
        return MailtoParameters.Contains(separator < 0 ? parameter : parameter[..separator]);
    }

    // Uri has already normalised other notations of an IP address (decimal, hexadecimal, shortened).
    private static bool IsLocalNetworkHost(Uri uri)
    {
        string host = uri.IdnHost.TrimEnd('.');
        if (host.Equals(LocalhostName, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(LocalhostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IsLocalNetworkAddress(address);
    }

    private static bool IsLocalNetworkAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fe80::/10, fec0::/10, fc00::/7
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 0
            || bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
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

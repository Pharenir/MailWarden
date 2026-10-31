namespace MailWarden.Core.Classification;

/// <summary>
/// Values precomputed once per message and shared by all signals.
/// </summary>
/// <param name="FromDomain">Domain of the From address (lower-case).</param>
/// <param name="FromOrganizationalDomain">Approximated organisational domain of <paramref name="FromDomain"/>.</param>
/// <param name="Authentication">Parsed top-most Authentication-Results header (Empty when there is none).</param>
/// <param name="AuthStatus">Overall authentication state derived from <paramref name="Authentication"/>.</param>
/// <param name="FromIsFreemail">True when the From domain or its organisational domain is a freemail domain.</param>
/// <param name="Settings">Validated classifier settings.</param>
public sealed record ClassificationContext(
    string FromDomain,
    string FromOrganizationalDomain,
    AuthenticationResults Authentication,
    AuthStatus AuthStatus,
    bool FromIsFreemail,
    ClassifierSettings Settings);

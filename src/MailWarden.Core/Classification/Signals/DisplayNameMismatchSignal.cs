namespace MailWarden.Core.Classification.Signals;

/// <summary>
/// "display-name-mismatch": the From display name contains an e-mail address or a domain-like token
/// (<c>label.tld</c>, ASCII labels, letter-only TLD of 2–24 characters) whose organisational domain differs from the
/// From organisational domain → +20. The first mismatching token is reported.
/// </summary>
internal sealed class DisplayNameMismatchSignal : ISpamSignal
{
    /// <summary>Signal id (part of the contract).</summary>
    public const string SignalId = "display-name-mismatch";

    private const int Points = 20;
    private const int MinimumTldLength = 2;
    private const int MaximumTldLength = 24;
    private const string TokenPunctuation = ".-@_+";

    /// <inheritdoc/>
    public string Id => SignalId;

    /// <inheritdoc/>
    public SignalResult? Evaluate(ClassificationInput input, ClassificationContext context)
    {
        foreach (var token in SplitTokens(input.FromName))
        {
            var mentionedDomain = ToDomainLikeValue(token);
            if (mentionedDomain is null)
            {
                continue;
            }

            var mentionedOrganizationalDomain = DomainNames.TryGetOrganizationalDomain(mentionedDomain);
            if (mentionedOrganizationalDomain is not null
                && !string.Equals(mentionedOrganizationalDomain, context.FromOrganizationalDomain, StringComparison.Ordinal))
            {
                return new SignalResult(
                    SignalId,
                    Points,
                    $"Display name mentions '{mentionedDomain}', which differs from the sender domain '{context.FromDomain}'.");
            }
        }

        return null;
    }

    // Hand-written split: letters, digits and ".-@_+" form tokens; everything else (spaces, <>, quotes, ...) separates.
    private static IEnumerable<string> SplitTokens(string displayName)
    {
        var tokenStart = -1;
        for (var index = 0; index <= displayName.Length; index++)
        {
            var isTokenCharacter = index < displayName.Length
                && (char.IsLetterOrDigit(displayName[index]) || TokenPunctuation.Contains(displayName[index], StringComparison.Ordinal));
            if (isTokenCharacter && tokenStart < 0)
            {
                tokenStart = index;
            }
            else if (!isTokenCharacter && tokenStart >= 0)
            {
                yield return displayName[tokenStart..index];
                tokenStart = -1;
            }
        }
    }

    // "service@paypal.test" → "paypal.test"; "PayPal.com." → "paypal.com"; anything not domain-like → null.
    private static string? ToDomainLikeValue(string token)
    {
        var atIndex = token.LastIndexOf('@');
        var candidate = (atIndex >= 0 ? token[(atIndex + 1)..] : token).Trim('.', '-').ToLowerInvariant();

        var labels = candidate.Split('.');
        if (labels.Length < 2 || labels.Any(label => label.Length == 0 || !label.All(IsAsciiLabelCharacter)))
        {
            return null;
        }

        var topLevelDomain = labels[^1];
        var isTldShapeValid = topLevelDomain.Length >= MinimumTldLength
            && topLevelDomain.Length <= MaximumTldLength
            && topLevelDomain.All(char.IsAsciiLetter);
        return isTldShapeValid ? candidate : null;
    }

    private static bool IsAsciiLabelCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character == '-';
}

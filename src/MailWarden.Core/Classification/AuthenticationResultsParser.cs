using System.Text;

namespace MailWarden.Core.Classification;

/// <summary>
/// Tolerant parser for the value of one Authentication-Results header (RFC 8601 and the non-compliant Outlook.com
/// form without authserv-id).
/// </summary>
public static class AuthenticationResultsParser
{
    // Method names that start a new result even without a preceding ';' (tolerance for sloppy headers).
    private static readonly string[] EvaluatedMethodNames = ["spf", "dkim", "dmarc"];

    /// <summary>
    /// Parses a header value. Comments "(...)" and quoted strings never split statements; method, verdict and property
    /// names are case-insensitive; folded whitespace and missing spaces after ';' are accepted; unknown methods
    /// (compauth, arc, bimi, ...) are ignored; unknown verdict words map to <see cref="AuthVerdict.Unknown"/>.
    /// A leading token without '=' before the first ';' is the AuthServId (except the RFC keyword "none").
    /// A property value is one contiguous run after "key=" up to whitespace, ';' or '(' outside quotes (quoted parts
    /// included, domain = part after the last '@'); a '=' or "dmarc="/"dkim="/"spf=" inside a value never starts a
    /// new pair or result.
    /// Only the first SPF and the first DMARC result are kept; all DKIM results are kept in header order.
    /// Never throws for malformed content – returns what could be parsed (worst case <see cref="AuthenticationResults.Empty"/>).
    /// </summary>
    /// <param name="headerValue">Raw header value (without the "Authentication-Results:" name).</param>
    /// <returns>The parsed results.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="headerValue"/> is null.</exception>
    public static AuthenticationResults Parse(string headerValue)
    {
        ArgumentNullException.ThrowIfNull(headerValue);

        var tokens = AuthenticationResultsTokenizer.Tokenize(headerValue);
        var collector = new ResultCollector();
        var statementStart = 0;
        var isFirstStatement = true;

        // Statements are separated by ';'; each one is processed on its own.
        while (statementStart <= tokens.Count)
        {
            var statementEnd = tokens.FindIndex(statementStart, token => token.Kind == HeaderTokenKind.Semicolon);
            if (statementEnd < 0)
            {
                statementEnd = tokens.Count;
            }

            var pairStart = statementStart;
            if (isFirstStatement && TryReadAuthServId(tokens, statementStart, statementEnd, out var authServId))
            {
                collector.AuthServId = authServId;
                pairStart++;
            }

            ParseStatement(tokens, pairStart, statementEnd, collector);
            isFirstStatement = false;
            statementStart = statementEnd + 1;
        }

        return collector.Build();
    }

    // Leading Word not followed by '=' in the first statement.
    private static bool TryReadAuthServId(List<HeaderToken> tokens, int start, int end, out string authServId)
    {
        authServId = string.Empty;
        if (start >= end || tokens[start].Kind != HeaderTokenKind.Word)
        {
            return false;
        }

        var isFollowedByEquals = start + 1 < end && tokens[start + 1].Kind == HeaderTokenKind.EqualsSign;
        var candidate = tokens[start].Text.Trim();
        if (isFollowedByEquals || candidate.Length == 0 || candidate.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        authServId = candidate;
        return true;
    }

    // Reads "key=value" pairs; the first pair is the method, later pairs are its properties, unless a later key is
    // spf/dkim/dmarc again (missing ';'). Stray tokens (version numbers) are skipped. A value is one contiguous run
    // (see ReadValue), so '=' or "dmarc=" inside a value never starts a new pair or result.
    private static void ParseStatement(List<HeaderToken> tokens, int start, int end, ResultCollector collector)
    {
        PendingMethod? currentMethod = null;
        var position = start;
        while (position < end)
        {
            var isPairStart = position + 2 < end
                && tokens[position].Kind == HeaderTokenKind.Word
                && tokens[position + 1].Kind == HeaderTokenKind.EqualsSign;
            if (!isPairStart)
            {
                position++;
                continue;
            }

            var key = tokens[position].Text;
            position = ReadValue(tokens, position + 2, end, out var value);

            if (currentMethod is null || IsEvaluatedMethodName(key))
            {
                collector.Add(currentMethod);
                currentMethod = new PendingMethod(GetMethodName(key), ParseVerdict(value));
            }
            else
            {
                currentMethod.SetProperty(key, value);
            }
        }

        collector.Add(currentMethod);
    }

    // RFC 8601 pvalue: the value after "key =" is one contiguous run up to whitespace, ';' or a comment (outside
    // quotes). Adjacent word, quoted and '=' parts are concatenated without separator, so a quoted local part stays
    // part of the value ("\"x@victim.test\"@attacker.test") and VERP/SRS local parts with '=' are not split.
    // start points at the first token after '=' (start < end); returns the index after the value. Linear: every
    // token is visited once.
    private static int ReadValue(List<HeaderToken> tokens, int start, int end, out string value)
    {
        var position = start + 1;
        while (position < end && tokens[position].IsJoinedToPrevious)
        {
            position++;
        }

        if (position == start + 1)
        {
            value = TokenText(tokens[start]);
            return position;
        }

        var builder = new StringBuilder();
        for (var index = start; index < position; index++)
        {
            builder.Append(TokenText(tokens[index]));
        }

        value = builder.ToString();
        return position;
    }

    // Semicolons never occur inside a statement, so only Word and '=' remain.
    private static string TokenText(HeaderToken token)
    {
        return token.Kind == HeaderTokenKind.EqualsSign ? "=" : token.Text;
    }

    // "dkim/1" → "dkim" (RFC 8601 method version), lower-case.
    private static string GetMethodName(string key)
    {
        var slashIndex = key.IndexOf('/', StringComparison.Ordinal);
        var name = slashIndex >= 0 ? key[..slashIndex] : key;
        return name.Trim().ToLowerInvariant();
    }

    private static bool IsEvaluatedMethodName(string key)
    {
        return Array.IndexOf(EvaluatedMethodNames, GetMethodName(key)) >= 0;
    }

    private static AuthVerdict ParseVerdict(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "none" => AuthVerdict.None,
            "pass" => AuthVerdict.Pass,
            "fail" or "hardfail" => AuthVerdict.Fail,
            "softfail" => AuthVerdict.SoftFail,
            "neutral" => AuthVerdict.Neutral,
            "temperror" => AuthVerdict.TempError,
            "permerror" => AuthVerdict.PermError,
            "policy" => AuthVerdict.Policy,
            _ => AuthVerdict.Unknown,
        };
    }

    // Value of a domain-bearing property → lower-case domain or null (address → part after the last '@').
    private static string? ToDomain(string? propertyValue)
    {
        if (propertyValue is null)
        {
            return null;
        }

        var trimmed = propertyValue.Trim().Trim('<', '>');
        var atIndex = trimmed.LastIndexOf('@');
        var domain = DomainNames.Normalize(atIndex >= 0 ? trimmed[(atIndex + 1)..] : trimmed).TrimStart('.');
        return domain.Length == 0 ? null : domain;
    }

    /// <summary>One method result while its properties are being read.</summary>
    private sealed class PendingMethod(string name, AuthVerdict verdict)
    {
        public string Name { get; } = name;

        public AuthVerdict Verdict { get; } = verdict;

        public string? SmtpMailFrom { get; private set; }

        public string? HeaderD { get; private set; }

        public string? HeaderI { get; private set; }

        public string? HeaderFrom { get; private set; }

        // First occurrence of a property wins; unknown properties (action, reason, header.b, ...) are ignored.
        public void SetProperty(string key, string value)
        {
            switch (key.Trim().ToLowerInvariant())
            {
                case "smtp.mailfrom":
                    SmtpMailFrom ??= value;
                    break;
                case "header.d":
                    HeaderD ??= value;
                    break;
                case "header.i":
                    HeaderI ??= value;
                    break;
                case "header.from":
                    HeaderFrom ??= value;
                    break;
            }
        }
    }

    /// <summary>Accumulates finished method results into the final record.</summary>
    private sealed class ResultCollector
    {
        private readonly List<AuthMethodResult> dkimResults = [];
        private AuthMethodResult? spfResult;
        private AuthMethodResult? dmarcResult;

        public string? AuthServId { get; set; }

        public void Add(PendingMethod? method)
        {
            switch (method?.Name)
            {
                case "spf":
                    spfResult ??= new AuthMethodResult(method.Verdict, ToDomain(method.SmtpMailFrom));
                    break;
                case "dkim":
                    dkimResults.Add(new AuthMethodResult(method.Verdict, ToDomain(method.HeaderD) ?? ToDomain(method.HeaderI)));
                    break;
                case "dmarc":
                    dmarcResult ??= new AuthMethodResult(method.Verdict, ToDomain(method.HeaderFrom));
                    break;
            }
        }

        public AuthenticationResults Build()
        {
            if (AuthServId is null && spfResult is null && dmarcResult is null && dkimResults.Count == 0)
            {
                return AuthenticationResults.Empty;
            }

            return new AuthenticationResults(AuthServId, spfResult, dkimResults.AsReadOnly(), dmarcResult);
        }
    }
}

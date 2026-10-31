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
    /// included); a '=' or "dmarc="/"dkim="/"spf=" inside a value never starts a new pair or result. The domain of a
    /// value is the part after the last '@' outside quotes; a value whose '@' signs are all quoted has no domain. A
    /// value that is exactly one quoted string is unquoted first.
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
    // (see ReadValue), so '=' or "dmarc=" inside a value never starts a new pair or result. "key=" at the end of the
    // statement is a present property with an empty value (C21).
    private static void ParseStatement(List<HeaderToken> tokens, int start, int end, ResultCollector collector)
    {
        PendingMethod? currentMethod = null;
        var position = start;
        while (position < end)
        {
            if (!IsKeyAt(tokens, position, end))
            {
                position++;
                continue;
            }

            var key = tokens[position].Text;
            var startsMethod = currentMethod is null || IsEvaluatedMethodName(key);
            position = ReadValue(tokens, position + 2, end, isPropertyValue: !startsMethod, out var value);

            if (currentMethod is not null && !startsMethod)
            {
                currentMethod.SetProperty(key, value);
            }
            else
            {
                collector.Add(currentMethod);
                currentMethod = new PendingMethod(GetMethodName(key), ParseVerdict(value.Text));
            }
        }

        collector.Add(currentMethod);
    }

    // A Word directly followed by '=' (whitespace in between allowed, e.g. "dkim = pass").
    private static bool IsKeyAt(List<HeaderToken> tokens, int position, int end)
    {
        return position + 1 < end
            && tokens[position].Kind == HeaderTokenKind.Word
            && tokens[position + 1].Kind == HeaderTokenKind.EqualsSign;
    }

    // RFC 8601 pvalue: the value after "key =" is one contiguous run up to whitespace, ';' or a comment (outside
    // quotes). Adjacent word, quoted and '=' parts are concatenated without separator, so a quoted local part stays
    // part of the value ("\"x@victim.test\"@attacker.test") and VERP/SRS local parts with '=' are not split.
    // C21: "key=" followed by whitespace and another "key=" (or by the statement end) is an empty value; the next
    // word starts a new pair ("smtp.mailfrom= dmarc=fail"). A first token after whitespace that is not a key is still
    // the value ("dkim = pass").
    // C20: CFWS between local part and "@domain" is ignored (RFC 5322): in a property value, a following word that
    // starts with '@' and is not itself a key is appended with its joined run ("\"x@victim.test\" (c) @attacker.test").
    // Domain part (C17): the text after the last '@' outside quotes, up to the next quoted part (a domain name never
    // contains a quoted string); "x@attacker.test\"@victim.test\"" → "attacker.test". A value whose '@' signs are all
    // inside quotes has no domain ("\"a\"\"b@c.test\"" → null); a value without any '@' is the domain itself
    // ("example.test", Outlook.com smtp.mailfrom, header.d, header.from). C19: a value that is exactly one quoted
    // string is unquoted first.
    // start points at the first token after '='; returns the index after the value. Linear: every token is visited
    // once and its text scanned once.
    private static int ReadValue(List<HeaderToken> tokens, int start, int end, bool isPropertyValue, out PropertyValue value)
    {
        var isEmptyValue = start >= end || (!tokens[start].IsJoinedToPrevious && IsKeyAt(tokens, start, end));
        if (isEmptyValue)
        {
            value = new PropertyValue(string.Empty, null);
            return start;
        }

        var position = start + 1;
        while (position < end)
        {
            var token = tokens[position];
            var continuesValue = token.IsJoinedToPrevious
                || (isPropertyValue && IsAddressDomainContinuation(tokens, position, end));
            if (!continuesValue)
            {
                break;
            }

            position++;
        }

        // C19: a value that is exactly one quoted string is unquoted first (RFC 8601 "value = quoted-string"), then
        // C17 applies to its content ("\"Bounce@Example.TEST\"" → "example.test"). Mixed values stay with C17.
        var isSingleQuotedString = position == start + 1 && tokens[start].IsQuoted;

        var builder = new StringBuilder();
        var domainStart = -1;
        var domainEnd = -1;
        var hasQuotedAt = false;
        for (var index = start; index < position; index++)
        {
            var token = tokens[index];
            var text = TokenText(token);
            if (token.IsQuoted && !isSingleQuotedString)
            {
                if (domainStart >= 0 && domainEnd < 0)
                {
                    domainEnd = builder.Length;
                }

                hasQuotedAt |= text.Contains('@', StringComparison.Ordinal);
            }
            else
            {
                var atIndex = text.LastIndexOf('@');
                if (atIndex >= 0)
                {
                    domainStart = builder.Length + atIndex + 1;
                    domainEnd = -1;
                }
            }

            builder.Append(text);
        }

        var valueText = builder.ToString();
        string? domainPart;
        if (domainStart >= 0)
        {
            domainPart = valueText[domainStart..(domainEnd >= 0 ? domainEnd : valueText.Length)];
        }
        else
        {
            domainPart = hasQuotedAt ? null : valueText;
        }

        value = new PropertyValue(valueText, domainPart);
        return position;
    }

    // C20: a separated Word starting with '@' that is not a key belongs to the preceding address value. Quoted words
    // count too: appending them makes the value mixed, whose quoted '@' never yields a domain (fail-safe).
    private static bool IsAddressDomainContinuation(List<HeaderToken> tokens, int position, int end)
    {
        var token = tokens[position];
        return token.Kind == HeaderTokenKind.Word
            && token.Text.StartsWith('@')
            && !IsKeyAt(tokens, position, end);
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

    // Domain-bearing property → lower-case domain or null (property absent, or no domain part per C17).
    private static string? ToDomain(PropertyValue? propertyValue)
    {
        var domainPart = propertyValue?.DomainPart;
        if (domainPart is null)
        {
            return null;
        }

        var domain = DomainNames.Normalize(domainPart.Trim().Trim('<', '>')).TrimStart('.');
        return domain.Length == 0 ? null : domain;
    }

    /// <summary>One property value as read from the header.</summary>
    /// <param name="Text">The joined value (quotes removed).</param>
    /// <param name="DomainPart">Raw domain part per C17, or null when the value has no domain.</param>
    private readonly record struct PropertyValue(string Text, string? DomainPart);

    /// <summary>One method result while its properties are being read.</summary>
    private sealed class PendingMethod(string name, AuthVerdict verdict)
    {
        public string Name { get; } = name;

        public AuthVerdict Verdict { get; } = verdict;

        public PropertyValue? SmtpMailFrom { get; private set; }

        public PropertyValue? HeaderD { get; private set; }

        public PropertyValue? HeaderI { get; private set; }

        public PropertyValue? HeaderFrom { get; private set; }

        // First occurrence of a property wins (also when it has no domain); unknown properties (action, reason,
        // header.b, ...) are ignored.
        public void SetProperty(string key, PropertyValue value)
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
        private bool isDmarcHeaderFromWithoutDomain;

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
                case "dmarc" when dmarcResult is null:
                    var headerFromDomain = ToDomain(method.HeaderFrom);
                    dmarcResult = new AuthMethodResult(method.Verdict, headerFromDomain);
                    isDmarcHeaderFromWithoutDomain = method.HeaderFrom is not null && headerFromDomain is null;
                    break;
            }
        }

        public AuthenticationResults Build()
        {
            if (AuthServId is null && spfResult is null && dmarcResult is null && dkimResults.Count == 0)
            {
                return AuthenticationResults.Empty;
            }

            return new AuthenticationResults(AuthServId, spfResult, dkimResults.AsReadOnly(), dmarcResult)
            {
                IsDmarcHeaderFromWithoutDomain = isDmarcHeaderFromWithoutDomain,
            };
        }
    }
}

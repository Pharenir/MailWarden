namespace MailWarden.Core.Classification;

/// <summary>
/// Helpers for reason and override texts. Values that come from the message (domains, labels, hosts, display-name
/// tokens) are attacker-controlled and unbounded; they are truncated before they are copied into a reason, because
/// reasons are shown in the "Why?" view and persisted with the decision.
/// </summary>
internal static class ReasonText
{
    /// <summary>Maximum number of characters of a message value inside a reason text (before the ellipsis).</summary>
    public const int MaximumValueLength = 100;

    private const string Ellipsis = "…";

    /// <summary>Truncates a message value to <see cref="MaximumValueLength"/> characters and appends "…" when cut.</summary>
    /// <param name="value">Value from the message (not null).</param>
    /// <returns>The value unchanged when short enough, otherwise its first characters plus "…".</returns>
    public static string Truncate(string value)
    {
        if (value.Length <= MaximumValueLength)
        {
            return value;
        }

        // Never split a surrogate pair: a lone high surrogate would make the text invalid UTF-16.
        var cutLength = char.IsHighSurrogate(value[MaximumValueLength - 1]) ? MaximumValueLength - 1 : MaximumValueLength;
        return string.Concat(value.AsSpan(0, cutLength), Ellipsis);
    }
}

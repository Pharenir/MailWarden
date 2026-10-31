namespace MailWarden.Core.Classification;

/// <summary>
/// Helpers for reason and override texts. Values that come from the message (domains, labels, hosts, display-name
/// tokens) are attacker-controlled and unbounded; they are truncated before they are copied into a reason, because
/// reasons are shown in the "Why?" view and persisted with the decision.
/// </summary>
internal static class ReasonText
{
    /// <summary>Truncates a message value via <see cref="DisplayText.Truncate"/> (100 characters plus "…").</summary>
    /// <param name="value">Value from the message (not null).</param>
    /// <returns>The value unchanged when short enough, otherwise its first characters plus "…".</returns>
    public static string Truncate(string value) => DisplayText.Truncate(value);
}

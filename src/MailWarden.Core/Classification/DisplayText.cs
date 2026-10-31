namespace MailWarden.Core.Classification;

/// <summary>
/// Truncation of untrusted, unbounded values (message data, file content) before they are copied into texts that are
/// shown, persisted or logged (reason texts, error messages).
/// </summary>
public static class DisplayText
{
    /// <summary>Maximum number of characters of a value inside a text (before the ellipsis).</summary>
    public const int MaximumLength = 100;

    private const string Ellipsis = "…";

    /// <summary>
    /// Truncates a value to <see cref="MaximumLength"/> characters and appends "…" when cut; a surrogate pair is never
    /// split.
    /// </summary>
    /// <param name="value">Value to show.</param>
    /// <returns>The value unchanged when short enough, otherwise its first characters plus "…".</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static string Truncate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= MaximumLength)
        {
            return value;
        }

        // Never split a surrogate pair: a lone high surrogate would make the text invalid UTF-16.
        var cutLength = char.IsHighSurrogate(value[MaximumLength - 1]) ? MaximumLength - 1 : MaximumLength;
        return string.Concat(value.AsSpan(0, cutLength), Ellipsis);
    }
}

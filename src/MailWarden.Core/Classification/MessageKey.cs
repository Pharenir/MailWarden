using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MailWarden.Core.Model;

namespace MailWarden.Core.Classification;

/// <summary>
/// Identity of one delivery of a message across folders and devices, used so that it is decided only once. A hash, so the
/// decision log holds neither the Message-ID nor the sender. The sender controls the Message-ID and could reuse it for a
/// whole campaign; the top-most Received header is written by the receiving provider, differs per delivery and stays the
/// same when the mail is moved or copied. So a mail moved back by hand keeps its key, while a new mail with a reused
/// Message-ID gets a new one.
/// </summary>
public static class MessageKey
{
    private const string MessageIdPrefix = "m:";
    private const string HeaderHashPrefix = "h:";

    /// <summary>
    /// "m:" + lower-case SHA-256 hex of the trimmed Message-ID + LF + topReceived. Without a Message-ID (null or
    /// whitespace): "h:" + lower-case SHA-256 hex of FromAddress (lower-cased) + LF + Date (UTC, ISO 8601 round trip) + LF +
    /// Subject + LF + topReceived. The hash input is UTF-8. Size and folder are not part of it: Outlook.com changes the size
    /// on MOVE.
    /// </summary>
    /// <param name="message">Stored message.</param>
    /// <param name="topReceived">Top-most Received header; whitespace runs count as one space and the value is trimmed.
    /// Null or empty when there is none.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static string For(MailMessage message, string? topReceived)
    {
        ArgumentNullException.ThrowIfNull(message);

        string normalizedReceived = NormalizeWhitespace(topReceived);
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return MessageIdPrefix + HashHex($"{message.MessageId.Trim()}\n{normalizedReceived}");
        }

        string date = message.Date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return HeaderHashPrefix
            + HashHex($"{message.FromAddress.ToLowerInvariant()}\n{date}\n{message.Subject}\n{normalizedReceived}");
    }

    private static string NormalizeWhitespace(string? text)
    {
        return text is null
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string HashHex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

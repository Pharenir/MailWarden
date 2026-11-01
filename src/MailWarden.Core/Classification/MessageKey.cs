using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MailWarden.Core.Model;

namespace MailWarden.Core.Classification;

/// <summary>
/// Identity of a message across folders and devices, used so that a message is decided only once. A hash, so the
/// decision log holds neither the Message-ID nor the sender.
/// </summary>
public static class MessageKey
{
    private const string MessageIdPrefix = "m:";
    private const string HeaderHashPrefix = "h:";

    /// <summary>
    /// "m:" + lower-case SHA-256 hex of the trimmed Message-ID. Without a Message-ID (null or whitespace): "h:" +
    /// lower-case SHA-256 hex of FromAddress (lower-cased) + LF + Date (UTC, ISO 8601 round trip) + LF + Subject. Size and
    /// folder are not part of it: Outlook.com changes the size on MOVE.
    /// </summary>
    /// <param name="message">Stored message.</param>
    /// <returns>The key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static string For(MailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return MessageIdPrefix + HashHex(message.MessageId.Trim());
        }

        string date = message.Date.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return HeaderHashPrefix + HashHex($"{message.FromAddress.ToLowerInvariant()}\n{date}\n{message.Subject}");
    }

    private static string HashHex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

using MimeKit;

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// Parser settings for mail from untrusted senders. MimeKit parses nested parts recursively and allows a depth of 1024
/// by default, which overflows the stack of a thread-pool thread (an exception no catch block can handle). Every parse
/// of a stored or fetched mail uses these settings.
/// </summary>
public static class MimeParserDefaults
{
    /// <summary>Deepest MIME nesting that is parsed; deeper parts are kept as opaque content.</summary>
    public const int MaxMimeDepth = 32;

    /// <summary>Creates the parser settings.</summary>
    /// <returns>New options based on the MimeKit defaults with <see cref="MaxMimeDepth"/>.</returns>
    public static ParserOptions Create()
    {
        ParserOptions options = ParserOptions.Default.Clone();
        options.MaxMimeDepth = MaxMimeDepth;
        return options;
    }
}

namespace MailWarden.Infrastructure.Mime;

/// <summary>
/// The theme the application actually shows, as far as the reading document depends on it. "Follow Windows" is no value of
/// its own: the window reports what it resolved to.
/// </summary>
public enum ReadingTheme
{
    /// <summary>Light page; also the sheet of a mail with colors of its own.</summary>
    Light = 0,

    /// <summary>Dark page with light text for a mail without colors of its own.</summary>
    Dark = 1,
}

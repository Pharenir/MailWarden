using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using MailWarden.Core.Model;
using MailWarden.Core.Text;
using MailWarden.Infrastructure.Storage;

namespace MailWarden.Presentation;

/// <summary>
/// What may happen to an attachment when it is opened.
/// </summary>
public enum AttachmentRisk
{
    /// <summary>A harmless type: opens at once, unless the folder of the mail asks for a question.</summary>
    Harmless = 0,

    /// <summary>Everything not known either way: a question first.</summary>
    Ask = 1,

    /// <summary>Runs code when opened: can only be saved.</summary>
    SaveOnly = 2,
}

/// <summary>
/// Makes a file name for the disk from the name in a mail, which is foreign data.
/// </summary>
public static class AttachmentFileName
{
    // Well below the 255 characters of a file system, so that the folders above it still fit into the paths other programs accept.
    private const int MaxCharacters = 120;

    // A longer "extension" is no type of any program; such a name is cut like a name without one.
    private const int MaxKeptExtensionCharacters = 32;

    private const string AttachedMessageExtension = ".eml";
    private const char Replacement = '_';

    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly char[] TrimmedAtTheEnd = ['.', ' '];

    // Beside what Windows refuses: wide forms of quote, slash, colon and backslash, and the signs that stand for the backslash in Asian
    // code pages. A program that converts the name to its code page would get the real character from them.
    private static readonly FrozenSet<char> InvalidCharacters = Path.GetInvalidFileNameChars()
        .Concat(['\uFF02', '\uFF0F', '\uFF1A', '\uFF3C', '\u00A5', '\u20A9'])
        .ToFrozenSet();

    // Default-ignorable code points outside the categories of IsInvisible: they show nothing, so they could hide a part of the name.
    private static readonly (int First, int Last)[] InvisibleRanges =
    [
        (0x034F, 0x034F), // combining grapheme joiner
        (0x115F, 0x1160), // Hangul fillers
        (0x17B4, 0x17B5), // Khmer inherent vowels
        (0x180B, 0x180F), // Mongolian variation selectors
        (0x2800, 0x2800), // Braille blank
        (0x3164, 0x3164), // Hangul filler
        (0xFE00, 0xFE0F), // variation selectors
        (0xFFA0, 0xFFA0), // halfwidth Hangul filler
        (0x1D173, 0x1D17A), // musical format characters
        (0xE0100, 0xE01EF), // variation selectors supplement
    ];

    /// <summary>
    /// Makes a safe file name: only the last path part, no <c>:</c> and no other character Windows refuses, no control, bidi or
    /// other invisible characters, no trailing dots or spaces, no reserved device name, at most about 120 characters with the
    /// extension kept.
    /// </summary>
    /// <param name="name">Name from the mail; may be null or empty.</param>
    /// <param name="index">Position of the attachment; names the fallback.</param>
    /// <returns>The name, never empty; <c>anhang-&lt;index + 1&gt;</c> when nothing of the input remains.</returns>
    public static string ToSafe(string? name, int index)
    {
        string visible = KeepVisibleCharacters(SafeDisplayText.Clean(name ?? string.Empty));
        string lastPart = visible[(visible.LastIndexOfAny(PathSeparators) + 1)..];
        string replaced = string.Concat(lastPart.Select(character => InvalidCharacters.Contains(character) ? Replacement : character));
        string limited = LimitLength(TrimEdges(replaced));
        if (limited.Length == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"anhang-{index + 1}");
        }

        return DataPaths.IsDosDeviceName(limited) ? $"{Replacement}{limited}" : limited;
    }

    /// <summary>
    /// Like <see cref="ToSafe"/> for an attached message: it is written as a message, so that is its type whatever its name
    /// claims.
    /// </summary>
    /// <param name="name">Name from the mail; may be null or empty.</param>
    /// <param name="index">Position of the attachment; names the fallback.</param>
    /// <returns>A safe name ending in <c>.eml</c>.</returns>
    public static string ToSafeForMessage(string? name, int index)
    {
        string safeName = ToSafe(name, index);
        return safeName.EndsWith(AttachedMessageExtension, StringComparison.OrdinalIgnoreCase)
            ? safeName
            : ToSafe(safeName + AttachedMessageExtension, index);
    }

    // After SafeDisplayText.Clean this still takes the format characters it leaves for text display (joiners), other invisible
    // ones and broken surrogates, by whole code points. Every kind of space becomes a plain one, so that the trimming sees it.
    private static string KeepVisibleCharacters(string text)
    {
        var visible = new StringBuilder(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            OperationStatus status = Rune.DecodeFromUtf16(text.AsSpan(index), out Rune codePoint, out int consumedCharacters);
            index += consumedCharacters;
            if (status != OperationStatus.Done)
            {
                continue;
            }

            if (Rune.IsWhiteSpace(codePoint))
            {
                visible.Append(' ');
            }
            else if (!IsInvisible(codePoint))
            {
                visible.Append(codePoint.ToString());
            }
        }

        return visible.ToString();
    }

    private static bool IsInvisible(Rune codePoint)
    {
        return Rune.GetUnicodeCategory(codePoint)
                is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            || InvisibleRanges.Any(range => codePoint.Value >= range.First && codePoint.Value <= range.Last);
    }

    private static string TrimEdges(string text) => text.TrimStart(' ').TrimEnd(TrimmedAtTheEnd);

    // A cut leaves the extension at the end, because that is what Windows looks at. What comes out is classified after this.
    private static string LimitLength(string text)
    {
        if (text.Length <= MaxCharacters)
        {
            return text;
        }

        int extensionStart = text.LastIndexOf('.');
        int extensionLength = extensionStart < 0 ? 0 : text.Length - extensionStart;
        if (extensionLength > MaxKeptExtensionCharacters)
        {
            extensionLength = 0;
        }

        string head = TrimEdges(CutWithoutSplittingSurrogate(text, MaxCharacters - extensionLength));
        return TrimEdges(string.Concat(head, text.AsSpan(text.Length - extensionLength)));
    }

    private static string CutWithoutSplittingSurrogate(string text, int length)
    {
        return text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
    }
}

/// <summary>
/// The rules for opening an attachment. Only the extension decides, because that is what Windows uses to pick the program; the
/// MIME type a sender claims decides nothing.
/// </summary>
public static class AttachmentPolicy
{
    // Runs code, installs something, or mounts or points to something that does.
    private static readonly FrozenSet<string> SaveOnlyExtensions = new[]
    {
        // Programs and libraries
        "exe", "com", "scr", "pif", "cpl", "dll", "ocx", "sys", "drv", "msc", "gadget", "application", "appref-ms", "xbap", "vsto", "vsix", "app",
        "mlc", "msstyles", "webpnp", "ops", "osd", "osdx", "fxp", "prg", "plg", "pcd",

        // Scripts
        "bat", "cmd", "ps1", "ps1xml", "ps2", "ps2xml", "psc1", "psc2", "psd1", "psm1", "cdxml", "vb", "vbs", "vbe", "js", "jse",
        "ws", "wsf", "wsh", "wsc", "sct", "hta", "py", "pyc", "pyo", "pyw", "pyz", "pyzw", "pl", "jar", "jnlp", "sh", "ksh", "csh", "pssc",
        "msh", "msh1", "msh2", "mshxml", "msh1xml", "msh2xml", "bas", "vbp",

        // Installers and packages
        "msi", "msp", "mst", "msu", "appx", "appxbundle", "msix", "msixbundle", "appinstaller", "ms-appinstaller", "inf", "ins", "isp", "reg", "cab",
        "winget", "ppkg", "printerexport",

        // Links and shell commands
        "lnk", "url", "website", "scf", "shb", "shs", "settingcontent-ms", "library-ms", "searchconnector-ms", "search-ms", "desklink", "mapimail", "xnk",
        "diagcab", "diagcfg", "diagpkg", "job", "theme", "themepack", "deskthemepack", "rdp", "wsb", "msrcincident", "pbk", "udl", "prf", "rat", "wcx",
        "appcontent-ms", "grp",

        // Disk images
        "iso", "img", "vhd", "vhdx", "wim", "nrg", "ccd", "mds",

        // Certificates: opening one offers to install it
        "cer", "crt", "der", "p7b", "pfx", "p12", "sst",

        // Help with scripts
        "chm", "hlp", "htc", "its", "cnt", "hpj",

        // Office add-ins, macro projects and databases that run on opening
        "xll", "xla", "xlam", "wll", "ppa", "ppam", "mcf", "vsmacros", "vsw", "mdz",
        "ade", "adp", "mda", "mde", "mdb", "accda", "accdb", "accdc", "accde", "accdr", "accdt", "accdu", "accdw", "mdt", "mdw",
        "mad", "maf", "mag", "mam", "maq", "mar", "mas", "mau", "mav", "maw",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Kept short on purpose: whatever is not named here gets the question.
    private static readonly FrozenSet<string> HarmlessExtensions = new[]
    {
        "pdf", "txt", "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "docx", "xlsx", "pptx", "odt", "ods", "odp",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gives the risk level of a file name.</summary>
    /// <param name="safeFileName">A name from <see cref="AttachmentFileName.ToSafe"/>.</param>
    /// <returns>The level of the last extension; <see cref="AttachmentRisk.Ask"/> without one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="safeFileName"/> is null.</exception>
    public static AttachmentRisk Classify(string safeFileName)
    {
        ArgumentNullException.ThrowIfNull(safeFileName);

        // What Windows would drop does not count as the end of the name.
        string name = safeFileName.TrimEnd('.', ' ');
        string extension = name[(name.LastIndexOf('.') + 1)..];
        if (extension.Length == name.Length)
        {
            return AttachmentRisk.Ask;
        }

        if (SaveOnlyExtensions.Contains(extension))
        {
            return AttachmentRisk.SaveOnly;
        }

        return HarmlessExtensions.Contains(extension) ? AttachmentRisk.Harmless : AttachmentRisk.Ask;
    }

    /// <summary>
    /// Tells whether a question comes before opening: for <see cref="AttachmentRisk.Ask"/>, and for every type from Junk,
    /// Quarantine, Screener and Trash or an unknown folder. The role of the folder decides, never its name.
    /// </summary>
    /// <param name="risk">Risk level of the file.</param>
    /// <param name="folderRole">Role of the folder of the mail; null when it is unknown.</param>
    /// <returns>True when the user has to confirm.</returns>
    public static bool MustAskBeforeOpening(AttachmentRisk risk, FolderRole? folderRole)
    {
        return risk == AttachmentRisk.Ask
            || folderRole is null or FolderRole.Junk or FolderRole.Quarantine or FolderRole.Screener or FolderRole.Trash;
    }
}

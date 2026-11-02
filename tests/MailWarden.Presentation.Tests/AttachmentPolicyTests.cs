using System.Globalization;

namespace MailWarden.Presentation.Tests;

/// <summary>
/// AP-04c cases 1-6 (A4, A5, A6, A10): safe file names, risk levels by extension and the rule when to ask before opening.
/// Pure functions, no I/O. All names are artificial.
/// </summary>
[Trait("Category", "feature:ui")]
public sealed class AttachmentPolicyTests
{
    private static readonly string[] ReservedStems =
    [
        "CON", "PRN", "AUX", "NUL",
        .. Enumerable.Range(0, 10).Select(number => $"COM{number.ToString(CultureInfo.InvariantCulture)}"),
        .. Enumerable.Range(0, 10).Select(number => $"LPT{number.ToString(CultureInfo.InvariantCulture)}"),
    ];

    /// <summary>Bidirectional formatting characters that must never survive in a file name.</summary>
    private static readonly char[] BidiCharacters =
    [
        '\u061C', '\u200E', '\u200F', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069',
    ];

    /// <summary>Asserts everything that makes a name safe on Windows, whatever the input was.</summary>
    private static void AssertSafe(string safe)
    {
        Assert.False(string.IsNullOrWhiteSpace(safe), "the safe name is empty");
        Assert.DoesNotContain(safe, character => character is '\\' or '/' or ':');
        Assert.DoesNotContain(safe, character => Path.GetInvalidFileNameChars().Contains(character));
        Assert.DoesNotContain(safe, character => char.IsControl(character));
        Assert.DoesNotContain(safe, character => BidiCharacters.Contains(character));
        Assert.False(safe.EndsWith('.') || safe.EndsWith(' '), $"'{safe}' ends with a dot or a space");
        Assert.NotEqual(".", safe);
        Assert.NotEqual("..", safe);
        Assert.DoesNotContain("..\\", safe, StringComparison.Ordinal);
        Assert.Equal(safe, Path.GetFileName(safe));
        Assert.True(safe.Length <= 255, $"length {safe.Length} is more than Windows accepts for one name");

        string stem = safe.Split('.')[0].TrimEnd(' ');
        Assert.DoesNotContain(ReservedStems, reserved => string.Equals(reserved, stem, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Case 1 --------------------------------------------------------------------------------------------

    // Case 1
    [Theory]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("C:\\x\\a.pdf")]
    [InlineData("a/b.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.pdf")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    [InlineData("a.pdf.")]
    [InlineData("a.pdf   ")]
    [InlineData("\\\\server\\share\\x.doc")]
    [InlineData("/etc/passwd")]
    [InlineData("name:with:colons.txt")]
    public void Hostile_names_become_safe(string raw)
    {
        string safe = AttachmentFileName.ToSafe(raw, 0);

        AssertSafe(safe);
    }

    // Case 1: the extension survives, because the risk level is decided by it.
    [Theory]
    [InlineData("..\\..\\evil.exe", ".exe")]
    [InlineData("C:\\x\\a.pdf", ".pdf")]
    [InlineData("a/b.txt", ".txt")]
    [InlineData("con.txt", ".txt")]
    [InlineData("NUL.pdf", ".pdf")]
    public void Names_with_a_path_or_a_reserved_stem_keep_their_extension(string raw, string extension)
    {
        string safe = AttachmentFileName.ToSafe(raw, 0);

        Assert.EndsWith(extension, safe, StringComparison.OrdinalIgnoreCase);
    }

    // Case 1: trailing dots and spaces are removed, nothing else changes.
    [Theory]
    [InlineData("a.pdf.", "a.pdf")]
    [InlineData("a.pdf   ", "a.pdf")]
    [InlineData("a.pdf. . ", "a.pdf")]
    public void Trailing_dots_and_spaces_are_removed(string raw, string expected)
    {
        Assert.Equal(expected, AttachmentFileName.ToSafe(raw, 0));
    }

    // Case 1: a clean name is not touched (cleaned, not cut).
    [Theory]
    [InlineData("report_a.pdf")]
    [InlineData("Rechnung M\u00E4rz 2026.pdf")]
    [InlineData("archive.tar.gz")]
    public void Clean_names_stay_unchanged(string raw)
    {
        Assert.Equal(raw, AttachmentFileName.ToSafe(raw, 3));
    }

    // Case 1: empty results get the fallback name that uses index + 1.
    [Theory]
    [InlineData(null, 0, "anhang-1")]
    [InlineData("", 0, "anhang-1")]
    [InlineData("   ", 2, "anhang-3")]
    [InlineData(".", 4, "anhang-5")]
    [InlineData("..", 4, "anhang-5")]
    [InlineData("....", 1, "anhang-2")]
    [InlineData(". . .", 1, "anhang-2")]
    public void Empty_results_get_the_fallback_name(string? raw, int index, string expected)
    {
        Assert.Equal(expected, AttachmentFileName.ToSafe(raw, index));
    }

    // Case 1: a name made only of removed characters is empty after the cleaning and gets the fallback too.
    [Fact]
    public void A_name_of_only_control_and_bidi_characters_gets_the_fallback()
    {
        Assert.Equal("anhang-8", AttachmentFileName.ToSafe("\u202E\u0000\u0007\u2066", 7));
    }

    // Case 1: 400 characters.
    [Fact]
    public void A_name_of_400_characters_is_shortened_and_keeps_its_extension()
    {
        string raw = new string('a', 396) + ".pdf";

        string safe = AttachmentFileName.ToSafe(raw, 0);

        AssertSafe(safe);
        Assert.EndsWith(".pdf", safe, StringComparison.Ordinal);
        Assert.True(safe.Length < raw.Length);
    }

    // Case 1: 400 characters without an extension and with a long extension.
    [Theory]
    [InlineData(400, 0)]
    [InlineData(300, 120)]
    public void Long_names_without_or_with_a_long_extension_are_safe(int stemLength, int extensionLength)
    {
        string raw = new string('b', stemLength) + (extensionLength > 0 ? "." + new string('x', extensionLength) : string.Empty);

        AssertSafe(AttachmentFileName.ToSafe(raw, 0));
    }

    // Case 1: the same input always gives the same name (no random part), and the index only matters for the fallback.
    [Fact]
    public void The_result_is_deterministic_and_independent_of_the_index_for_ordinary_names()
    {
        Assert.Equal(AttachmentFileName.ToSafe("..\\x\\a.pdf", 0), AttachmentFileName.ToSafe("..\\x\\a.pdf", 5));
        Assert.Equal(AttachmentFileName.ToSafe("CON", 1), AttachmentFileName.ToSafe("CON", 1));
    }

    // ---- Case 2 --------------------------------------------------------------------------------------------

    // Case 2: right-to-left override hides the real extension; after the removal Windows sees .exe and so does Classify.
    [Fact]
    public void Rtlo_is_removed_and_the_classified_extension_is_the_one_windows_sees()
    {
        string safe = AttachmentFileName.ToSafe("invoice\u202Efdp.exe", 0);

        AssertSafe(safe);
        Assert.EndsWith(".exe", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AttachmentRisk.SaveOnly, AttachmentPolicy.Classify(safe));
    }

    // Case 2
    [Theory]
    [InlineData("a\u0000b.pdf")]
    [InlineData("a\u0007\u001Fb.pdf")]
    [InlineData("a\u0085b.pdf")]
    [InlineData("a\u200Eb\u200F.pdf")]
    [InlineData("a\u2066b\u2069.pdf")]
    [InlineData("a\u202Ab\u202C.pdf")]
    [InlineData("a\u061Cb.pdf")]
    [InlineData("tab\there.pdf")]
    [InlineData("line\r\nbreak.pdf")]
    public void Control_and_bidi_characters_are_removed(string raw)
    {
        string safe = AttachmentFileName.ToSafe(raw, 0);

        AssertSafe(safe);
        Assert.EndsWith(".pdf", safe, StringComparison.Ordinal);
    }

    // Case 2: the extension that is classified is always the last one of the safe name, whatever was hidden before.
    [Theory]
    [InlineData("photo\u202Egpj.exe", AttachmentRisk.SaveOnly)]
    [InlineData("rechnung.pdf\u202E.exe", AttachmentRisk.SaveOnly)]
    [InlineData("readme.exe\u0000.txt", AttachmentRisk.Harmless)]
    public void The_risk_follows_the_last_extension_of_the_cleaned_name(string raw, AttachmentRisk expected)
    {
        string safe = AttachmentFileName.ToSafe(raw, 0);

        AssertSafe(safe);
        Assert.Equal(expected, AttachmentPolicy.Classify(safe));
    }

    // ---- Case 3 --------------------------------------------------------------------------------------------

    // Case 3: every blocked type of A5.
    [Theory]
    [InlineData("exe")]
    [InlineData("com")]
    [InlineData("scr")]
    [InlineData("pif")]
    [InlineData("bat")]
    [InlineData("cmd")]
    [InlineData("ps1")]
    [InlineData("vbs")]
    [InlineData("js")]
    [InlineData("jse")]
    [InlineData("wsf")]
    [InlineData("hta")]
    [InlineData("msi")]
    [InlineData("lnk")]
    [InlineData("url")]
    [InlineData("reg")]
    [InlineData("jar")]
    [InlineData("cpl")]
    [InlineData("dll")]
    [InlineData("iso")]
    [InlineData("img")]
    [InlineData("vhd")]
    [InlineData("chm")]
    [InlineData("appinstaller")]
    [InlineData("msix")]
    public void Blocked_types_can_only_be_saved(string extension)
    {
        Assert.Equal(AttachmentRisk.SaveOnly, AttachmentPolicy.Classify($"file_a.{extension}"));
        Assert.Equal(AttachmentRisk.SaveOnly, AttachmentPolicy.Classify($"FILE_A.{extension.ToUpperInvariant()}"));
    }

    // Case 3
    [Theory]
    [InlineData("setup.EXE")]
    [InlineData("setup.Exe")]
    [InlineData("rechnung.pdf.exe")]
    [InlineData("rechnung.docx.scr")]
    [InlineData("a.b.c.ps1")]
    [InlineData("archive.zip.js")]
    public void Case_and_double_extensions_do_not_hide_a_blocked_type(string name)
    {
        Assert.Equal(AttachmentRisk.SaveOnly, AttachmentPolicy.Classify(name));
    }

    // ---- Case 4 --------------------------------------------------------------------------------------------

    // Case 4
    [Theory]
    [InlineData("pdf")]
    [InlineData("txt")]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    [InlineData("bmp")]
    [InlineData("webp")]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    [InlineData("odt")]
    [InlineData("zip")]
    public void Harmless_types_are_classified_harmless_in_any_case(string extension)
    {
        Assert.Equal(AttachmentRisk.Harmless, AttachmentPolicy.Classify($"file_a.{extension}"));
        Assert.Equal(AttachmentRisk.Harmless, AttachmentPolicy.Classify($"FILE_A.{extension.ToUpperInvariant()}"));
    }

    // ---- Case 5 --------------------------------------------------------------------------------------------

    // Case 5
    [Theory]
    [InlineData("makro.docm")]
    [InlineData("tabelle.xlsm")]
    [InlineData("seite.html")]
    [InlineData("bild.svg")]
    [InlineData("daten.xyz")]
    [InlineData("readme")]
    [InlineData("anhang-1")]
    public void Macro_formats_unknown_types_and_names_without_extension_ask_first(string name)
    {
        Assert.Equal(AttachmentRisk.Ask, AttachmentPolicy.Classify(name));
    }

    // Case 5: only the last extension counts.
    [Fact]
    public void An_exe_in_the_middle_of_the_name_does_not_make_it_blocked()
    {
        Assert.Equal(AttachmentRisk.Harmless, AttachmentPolicy.Classify("exe.pdf"));
    }

    // Case 5: a message part is saved as .eml and asks first (A11).
    [Fact]
    public void An_eml_file_asks_first()
    {
        Assert.Equal(AttachmentRisk.Ask, AttachmentPolicy.Classify("weitergeleitet.eml"));
    }

    // ---- Case 6 --------------------------------------------------------------------------------------------

    private static readonly FolderRole[] RiskyRoles = [FolderRole.Junk, FolderRole.Quarantine, FolderRole.Screener];

    // Case 6 (A5, A6, A10): the complete table for Ask and Harmless over every role and the unknown role (null).
    [Fact]
    public void The_question_table_holds_for_every_role()
    {
        foreach (FolderRole role in Enum.GetValues<FolderRole>())
        {
            Assert.True(AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.Ask, role), $"Ask in {role}");
            Assert.Equal(
                RiskyRoles.Contains(role),
                AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.Harmless, role));
        }

        Assert.True(AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.Ask, null));
        Assert.True(AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.Harmless, null));
    }

    // Case 6: Harmless asks in exactly the three risky roles and the unknown one, nowhere else.
    [Theory]
    [InlineData(FolderRole.Inbox, false)]
    [InlineData(FolderRole.Sent, false)]
    [InlineData(FolderRole.Drafts, false)]
    [InlineData(FolderRole.Archive, false)]
    [InlineData(FolderRole.Trash, false)]
    [InlineData(FolderRole.Outbox, false)]
    [InlineData(FolderRole.None, false)]
    [InlineData(FolderRole.Junk, true)]
    [InlineData(FolderRole.Quarantine, true)]
    [InlineData(FolderRole.Screener, true)]
    public void Harmless_asks_only_in_junk_quarantine_and_screener(FolderRole role, bool expected)
    {
        Assert.Equal(expected, AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.Harmless, role));
    }

    // Case 6: SaveOnly is never opened, so the question is moot; the call must at least be safe for every role.
    [Fact]
    public void The_question_for_a_blocked_type_does_not_throw()
    {
        foreach (FolderRole role in Enum.GetValues<FolderRole>())
        {
            Exception? failure = Record.Exception(() => AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.SaveOnly, role));
            Assert.Null(failure);
        }

        Assert.Null(Record.Exception(() => AttachmentPolicy.MustAskBeforeOpening(AttachmentRisk.SaveOnly, null)));
    }
}

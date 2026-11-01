using System.Globalization;
using MailWarden.Core.Model;
using MailWarden.Infrastructure.Background;
using MailWarden.Infrastructure.Sync;

namespace MailWarden.Presentation;

/// <summary>
/// All German texts of the user interface. Everything here is fixed text; mail data only ever appears in the places that
/// clean it (<see cref="MailWarden.Core.Text.SafeDisplayText"/>).
/// </summary>
public static class UiText
{
    /// <summary>Window title.</summary>
    public static string AppTitle => "MailWarden";

    /// <summary>Row or header without a subject.</summary>
    public static string NoSubject => "(Kein Betreff)";

    /// <summary>Attachment without a file name.</summary>
    public static string UnnamedAttachment => "(Ohne Namen)";

    /// <summary>Toolbar: switch between the two layouts.</summary>
    public static string ToggleLayout => "Ansicht wechseln";

    /// <summary>Toolbar: fetch mail now.</summary>
    public static string Refresh => "Aktualisieren";

    /// <summary>Toolbar: label of the theme choice.</summary>
    public static string ThemeLabel => "Design";

    /// <summary>Theme choice: follow Windows.</summary>
    public static string ThemeSystem => "System";

    /// <summary>Theme choice: light.</summary>
    public static string ThemeLight => "Hell";

    /// <summary>Theme choice: dark.</summary>
    public static string ThemeDark => "Dunkel";

    /// <summary>Reading header: sender label.</summary>
    public static string FromLabel => "Von:";

    /// <summary>Reading header: recipient label.</summary>
    public static string ToLabel => "An:";

    /// <summary>Reading header: copy label.</summary>
    public static string CcLabel => "Cc:";

    /// <summary>Reading header: date label.</summary>
    public static string DateLabel => "Datum:";

    /// <summary>Reading header: attachment label.</summary>
    public static string AttachmentsLabel => "Anhänge:";

    /// <summary>Reading pane without a selected message.</summary>
    public static string ReadingEmpty => "Keine Nachricht ausgewählt.";

    /// <summary>Reading pane while a message loads.</summary>
    public static string ReadingLoading => "Nachricht wird geladen …";

    /// <summary>Reading pane for a message without a local copy.</summary>
    public static string ReadingNotDownloaded => "Diese Nachricht ist noch nicht heruntergeladen.";

    /// <summary>Reading pane after a failure.</summary>
    public static string ReadingError => "Diese Nachricht konnte nicht angezeigt werden.";

    /// <summary>Reading pane when the browser component cannot start.</summary>
    public static string ReadingViewUnavailable =>
        "Die Anzeige konnte nicht gestartet werden. Ist die WebView2-Runtime installiert?";

    /// <summary>Banner: no accounts yet.</summary>
    public static string BannerNoAccounts =>
        "Noch keine Konten vorhanden. Konten werden bis AP-04b mit der AuthProbe hinzugefügt.";

    /// <summary>Banner: the settings file could not be read.</summary>
    public static string BannerSettingsLoadFailed =>
        "Die Einstellungsdatei ist ungültig; es gelten die Standardwerte.";

    /// <summary>Banner: the settings could not be saved.</summary>
    public static string BannerSettingsSaveFailed => "Die Einstellungen konnten nicht gespeichert werden.";

    /// <summary>Banner: the navigation could not be loaded.</summary>
    public static string BannerNavigationFailed => "Die Ordnerliste konnte nicht geladen werden.";

    /// <summary>Banner: the message list could not be loaded.</summary>
    public static string BannerListFailed => "Die Nachrichtenliste konnte nicht geladen werden.";

    /// <summary>Banner: the filter is switched to act, but waits for the address book.</summary>
    public static string BannerClassificationWaiting =>
        "Der Spamfilter verschiebt noch nichts: Er wartet auf das Adressbuch.";

    /// <summary>Banner: the last classification passes have mail that stays where it is because of a problem.</summary>
    /// <param name="evaluationFailed">Mails that could not be evaluated, added over the accounts.</param>
    /// <param name="brakeHeld">Mails the brake held, added over the accounts.</param>
    /// <returns>The banner text.</returns>
    public static string BannerClassificationProblems(int evaluationFailed, int brakeHeld)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Spamfilter: nicht bewertet {evaluationFailed}, von der Bremse zurückgehalten {brakeHeld}. Diese Mails bleiben in ihrem Ordner.");
    }

    /// <summary>Error window: title.</summary>
    public static string StartErrorTitle => "MailWarden kann nicht starten";

    /// <summary>Error window: invalid data folder setting.</summary>
    public static string StartErrorInvalidDataFolder =>
        "Der Datenordner ist ungültig. Prüfe die Umgebungsvariable MAILWARDEN_DATA_DIR: Sie muss einen vollständigen, "
        + "beschreibbaren Pfad enthalten.";

    /// <summary>Error window: another instance runs.</summary>
    public static string StartErrorInUse =>
        "Der Datenordner wird bereits verwendet. Läuft MailWarden schon? Beende die andere Instanz und starte erneut.";

    /// <summary>Error window: database of a newer version.</summary>
    public static string StartErrorSchemaTooNew =>
        "Die Datenbank stammt von einer neueren MailWarden-Version. Aktualisiere MailWarden.";

    /// <summary>Error window: any other start failure.</summary>
    public static string StartErrorOther =>
        "Beim Start ist ein Fehler aufgetreten. Details stehen in der Protokolldatei im Datenordner (logs).";

    /// <summary>Error window: a settings file of the spam filter in the data folder is invalid.</summary>
    /// <param name="fileName">Name of the file, without a path.</param>
    /// <returns>The text.</returns>
    public static string StartErrorInvalidClassifierFile(string fileName)
    {
        return $"Die Datei {fileName} im Datenordner ist ungültig. Korrigiere sie und starte erneut.";
    }

    /// <summary>Navigation label of a folder role.</summary>
    /// <param name="role">One of <see cref="MailView.GroupRoles"/>.</param>
    /// <returns>The label.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The role has no group view.</exception>
    public static string RoleLabel(FolderRole role) => role switch
    {
        FolderRole.Inbox => "Posteingang",
        FolderRole.Sent => "Gesendet",
        FolderRole.Outbox => "Postausgang",
        FolderRole.Screener => "Screener",
        FolderRole.Junk => "Junk",
        FolderRole.Quarantine => "Quarantäne",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The role has no group view."),
    };

    /// <summary>Button text with the number of blocked remote resources.</summary>
    /// <param name="blockedCount">Number of blocked resources.</param>
    /// <returns>The button text.</returns>
    public static string LoadRemoteImages(int blockedCount)
    {
        return string.Create(CultureInfo.InvariantCulture, $"Bilder laden ({blockedCount} blockiert)");
    }

    /// <summary>Notice: a limit for unusual mails reduced the document.</summary>
    public static string ContentReduced =>
        "Diese Nachricht ist ungewöhnlich aufgebaut und wird nur vereinfacht angezeigt; ein Teil der Formatierung oder Bilder fehlt.";

    /// <summary>Notice: only the plain text is shown, because the document would take too long.</summary>
    public static string ContentShownAsText =>
        "Die Aufbereitung dieser Nachricht dauert zu lange; sie wird daher nur als Text angezeigt.";

    /// <summary>Notice: parts below the depth limit cannot be shown.</summary>
    public static string ContentHasOpaqueParts =>
        "Ein Teil dieser Nachricht ist zu tief verschachtelt und kann nicht angezeigt werden.";

    /// <summary>Addition to the sender line.</summary>
    /// <param name="count">Number of further mailboxes in the From header.</param>
    /// <returns>The addition.</returns>
    public static string MoreSenders(int count)
    {
        return string.Create(CultureInfo.InvariantCulture, $"+{count} weitere");
    }

    /// <summary>Last entry of a long attachment list.</summary>
    /// <param name="count">Number of attachments not listed.</param>
    /// <returns>The entry.</returns>
    public static string MoreAttachments(int count)
    {
        return string.Create(CultureInfo.InvariantCulture, $"und {count} weitere");
    }

    /// <summary>Status text of an account node: fixed text per state, plus counts during a sync.</summary>
    /// <param name="status">Status of the account, or null when the service has not reported one yet.</param>
    /// <returns>The status text.</returns>
    public static string AccountStatus(AccountSyncStatus? status)
    {
        if (status is null)
        {
            return "Noch nicht gestartet";
        }

        if (status.Progress is SyncProgress progress)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Wird abgeglichen: Ordner {progress.FolderNumber}/{progress.FolderCount}, {progress.Processed}/{progress.Candidates}");
        }

        return status.State switch
        {
            AccountSyncState.Idle => "Aktuell",
            AccountSyncState.Running => "Wird abgeglichen …",
            AccountSyncState.WaitingAfterError => "Fehler, neuer Versuch folgt",
            AccountSyncState.NeedsSignIn => "Anmeldung erforderlich",
            AccountSyncState.Stopped => "Angehalten",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status.State, "Unknown sync state."),
        };
    }
}

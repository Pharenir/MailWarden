namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Ordered list of schema migrations. Entry n migrates from version n-1 to n. <see cref="SqliteDatabase"/> runs each
/// pending entry in its own transaction and writes <c>schema_version</c> at the end of that transaction.
/// Never edit a released entry; append a new version instead.
/// </summary>
internal static class SchemaMigrations
{
    /// <summary>Version 1: accounts, folders, messages with full-text index, contacts.</summary>
    private const string Version1 = """
        CREATE TABLE schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE accounts (
            id            TEXT    NOT NULL PRIMARY KEY,
            display_name  TEXT    NOT NULL,
            email_address TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            provider      INTEGER NOT NULL
        );

        CREATE TABLE folders (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id   TEXT    NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            full_name    TEXT    NOT NULL,
            role         INTEGER NOT NULL,
            uid_validity INTEGER NOT NULL,
            UNIQUE (account_id, full_name),
            UNIQUE (id, account_id) -- parent key of the composite folder reference in messages
        );

        CREATE TABLE messages (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id      TEXT    NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            folder_id       INTEGER NOT NULL,
            uid             INTEGER NOT NULL,
            message_id      TEXT    NULL,
            subject         TEXT    NOT NULL,
            from_address    TEXT    NOT NULL,
            from_name       TEXT    NOT NULL,
            date            TEXT    NOT NULL, -- ISO 8601 round-trip format with offset ("O")
            flags           INTEGER NOT NULL,
            has_attachments INTEGER NOT NULL,
            size            INTEGER NOT NULL,
            eml_path        TEXT    NULL,
            text_preview    TEXT    NOT NULL,
            UNIQUE (folder_id, uid),
            -- The message account must be the account of its folder.
            FOREIGN KEY (folder_id, account_id) REFERENCES folders (id, account_id) ON DELETE CASCADE
        );

        -- date is text with its own offset, so lists sort by julianday(date) (UTC instant); the expression indexes
        -- serve that order (newest first, ties by id) without a temporary sort.
        CREATE INDEX ix_messages_folder_date ON messages (folder_id, julianday(date) DESC, id DESC);
        CREATE INDEX ix_messages_date ON messages (julianday(date) DESC, id DESC);

        -- rowid = messages.id; maintained only by MessageSearchIndex, in the same transaction as the row.
        CREATE VIRTUAL TABLE messages_fts USING fts5 (
            subject,
            from_address,
            from_name,
            body_text,
            tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TABLE contacts (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            email_address TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            display_name  TEXT    NOT NULL,
            source        INTEGER NOT NULL
        );
        """;

    /// <summary>Version 2: sync progress per folder (removed together with its folder).</summary>
    private const string Version2 = """
        CREATE TABLE folder_sync_state (
            folder_id          INTEGER NOT NULL PRIMARY KEY REFERENCES folders (id) ON DELETE CASCADE,
            highest_synced_uid INTEGER NOT NULL,
            highest_mod_seq    INTEGER NOT NULL,
            last_synced_at     TEXT    NULL -- ISO 8601 round-trip ("O")
        );
        """;

    /// <summary>
    /// Version 3: the sync state stores a fixed floor instead of a high-water mark. Old values are high-water marks
    /// that hide messages the server did not deliver, so they are reset to 0 (nothing skipped).
    /// </summary>
    private const string Version3 = """
        ALTER TABLE folder_sync_state RENAME COLUMN highest_synced_uid TO skipped_up_to_uid;
        UPDATE folder_sync_state SET skipped_up_to_uid = 0;
        """;

    /// <summary>All migrations in ascending order; versions are contiguous starting at 1.</summary>
    internal static readonly IReadOnlyList<(int Version, string Sql)> Ordered =
    [
        (1, Version1),
        (2, Version2),
        (3, Version3),
    ];
}

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
            UNIQUE (account_id, full_name)
        );

        CREATE TABLE messages (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id      TEXT    NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
            folder_id       INTEGER NOT NULL REFERENCES folders (id) ON DELETE CASCADE,
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
            UNIQUE (account_id, folder_id, uid)
        );

        CREATE INDEX ix_messages_folder_date ON messages (folder_id, date DESC);
        CREATE INDEX ix_messages_date ON messages (date DESC);

        -- rowid = messages.id; maintained by SqliteMessageStore in the same transaction as the row.
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

    /// <summary>All migrations in ascending order; versions are contiguous starting at 1.</summary>
    internal static readonly IReadOnlyList<(int Version, string Sql)> Ordered =
    [
        (1, Version1),
    ];
}

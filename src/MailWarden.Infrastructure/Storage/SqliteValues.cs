using System.Globalization;
using MailWarden.Core.Storage;
using Microsoft.Data.Sqlite;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// Conversions between model values and their SQLite column representation, shared by all SQLite stores.
/// Guids are stored as lower-case "D" text, dates as ISO 8601 round-trip text with offset, enums and flags as integers.
/// Reading fails with <see cref="StorageException"/> on values that cannot have been written by this code.
/// </summary>
internal static class SqliteValues
{
    /// <summary>Text form of an id column value.</summary>
    internal static string FromGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Round-trip text including the original offset, e.g. <c>2026-10-31T10:15:00.0000000+02:00</c>.</summary>
    internal static string FromDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    internal static Guid ReadGuid(SqliteDataReader reader, int ordinal)
    {
        string text = reader.GetString(ordinal);
        if (!Guid.TryParseExact(text, "D", out Guid value))
        {
            throw CorruptValue(reader, ordinal, text);
        }

        return value;
    }

    internal static DateTimeOffset ReadDate(SqliteDataReader reader, int ordinal)
    {
        string text = reader.GetString(ordinal);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset value))
        {
            throw CorruptValue(reader, ordinal, text);
        }

        return value;
    }

    internal static uint ReadUInt32(SqliteDataReader reader, int ordinal)
    {
        long value = reader.GetInt64(ordinal);
        if (value < uint.MinValue || value > uint.MaxValue)
        {
            throw CorruptValue(reader, ordinal, value.ToString(CultureInfo.InvariantCulture));
        }

        return (uint)value;
    }

    internal static bool ReadBoolean(SqliteDataReader reader, int ordinal)
    {
        long value = reader.GetInt64(ordinal);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw CorruptValue(reader, ordinal, value.ToString(CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>Reads a plain enum and rejects numbers that are not defined members.</summary>
    internal static TEnum ReadEnum<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum
    {
        long value = reader.GetInt64(ordinal);
        if (value < int.MinValue || value > int.MaxValue || !Enum.IsDefined(typeof(TEnum), (int)value))
        {
            throw CorruptValue(reader, ordinal, value.ToString(CultureInfo.InvariantCulture));
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), (int)value);
    }

    internal static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Wraps a SQLite failure so callers only need to handle <see cref="StorageException"/>.</summary>
    /// <param name="operation">What was attempted, e.g. "Storing the account".</param>
    /// <param name="exception">Original SQLite exception (message contains the constraint name, no values).</param>
    internal static StorageException Wrap(string operation, SqliteException exception) =>
        new($"{operation} failed: {exception.Message}", exception);

    private static StorageException CorruptValue(SqliteDataReader reader, int ordinal, string value) =>
        new($"Column '{reader.GetName(ordinal)}' contains an unexpected value '{value}'.");
}

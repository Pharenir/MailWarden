using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MailWarden.Core.Model;

namespace MailWarden.Core.Tests.Classification;

/// <summary>AP-07c D1 / case 2: <see cref="MessageKey.For"/> is stable across folders, sizes and whitespace.</summary>
[Trait("Category", "feature:classifier")]
public sealed class MessageKeyTests
{
    private static readonly DateTimeOffset BaseDate = new(2026, 3, 4, 10, 30, 0, TimeSpan.FromHours(2));

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static MailMessage Message(
        string? messageId,
        string fromAddress = "sender_a@example.test",
        string subject = "sample_subject",
        DateTimeOffset? date = null,
        long folderId = 1,
        uint uid = 1,
        long size = 1000) =>
        new(0, Guid.Parse("11111111-2222-3333-4444-555555555555"), folderId, uid, messageId, subject, fromAddress,
            "Sender A", date ?? BaseDate, MessageFlags.None, false, size, null, "preview");

    // Case 2
    [Fact]
    public void Key_with_message_id_is_m_prefix_and_lower_case_sha256_of_the_id()
    {
        string key = MessageKey.For(Message("<msg_a@example.test>"));

        Assert.Equal("m:" + Sha256Hex("<msg_a@example.test>"), key);
        Assert.Equal(key.ToLowerInvariant(), key);
        Assert.Equal(2 + 64, key.Length);
    }

    // Case 2: Outlook.com changes the size on MOVE; folder and UID differ after a move as well
    [Fact]
    public void Size_folder_and_uid_are_not_part_of_the_key()
    {
        string first = MessageKey.For(Message("<msg_a@example.test>", folderId: 1, uid: 5, size: 1000));
        string second = MessageKey.For(Message("<msg_a@example.test>", folderId: 9, uid: 77, size: 4321));

        Assert.Equal(first, second);
    }

    // Case 2
    [Fact]
    public void Surrounding_whitespace_of_the_message_id_is_trimmed()
    {
        string plain = MessageKey.For(Message("<msg_a@example.test>"));
        string padded = MessageKey.For(Message("  \t<msg_a@example.test>\r\n "));

        Assert.Equal(plain, padded);
    }

    [Fact]
    public void Different_message_ids_give_different_keys()
    {
        Assert.NotEqual(
            MessageKey.For(Message("<msg_a@example.test>")),
            MessageKey.For(Message("<msg_b@example.test>")));
    }

    [Fact]
    public void Subject_sender_and_date_do_not_matter_when_a_message_id_exists()
    {
        string first = MessageKey.For(Message("<msg_a@example.test>", "sender_a@example.test", "subject_one", BaseDate));
        string second = MessageKey.For(Message("<msg_a@example.test>", "sender_b@example.test", "subject_two", BaseDate.AddDays(3)));

        Assert.Equal(first, second);
    }

    // Case 2
    [Fact]
    public void Key_without_message_id_is_h_prefix_and_sha256_of_from_date_and_subject()
    {
        string expectedInput = "sender_a@example.test" + "\n"
            + BaseDate.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "\n"
            + "sample_subject";

        string key = MessageKey.For(Message(null, "sender_a@example.test", "sample_subject", BaseDate));

        Assert.Equal("h:" + Sha256Hex(expectedInput), key);
    }

    // Case 2: stable
    [Fact]
    public void Key_without_message_id_is_stable_across_calls_and_folders()
    {
        string first = MessageKey.For(Message(null, folderId: 1, uid: 1, size: 10));
        string second = MessageKey.For(Message(null, folderId: 2, uid: 99, size: 20));

        Assert.StartsWith("h:", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    // Case 2: the address case does not matter
    [Fact]
    public void Sender_address_case_does_not_matter_without_message_id()
    {
        string lower = MessageKey.For(Message(null, "sender_a@example.test"));
        string mixed = MessageKey.For(Message(null, "Sender_A@Example.TEST"));

        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void Same_instant_in_another_offset_gives_the_same_key_without_message_id()
    {
        DateTimeOffset inUtc = BaseDate.ToUniversalTime();

        Assert.Equal(
            MessageKey.For(Message(null, date: BaseDate)),
            MessageKey.For(Message(null, date: inUtc)));
    }

    [Fact]
    public void Subject_sender_and_date_matter_without_message_id()
    {
        string baseKey = MessageKey.For(Message(null));

        Assert.NotEqual(baseKey, MessageKey.For(Message(null, subject: "other_subject")));
        Assert.NotEqual(baseKey, MessageKey.For(Message(null, date: BaseDate.AddSeconds(1))));
        Assert.NotEqual(baseKey, MessageKey.For(Message(null, fromAddress: "sender_b@example.test")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t ")]
    public void Empty_or_whitespace_message_id_counts_as_absent(string messageId)
    {
        Assert.Equal(MessageKey.For(Message(null)), MessageKey.For(Message(messageId)));
        Assert.StartsWith("h:", MessageKey.For(Message(messageId)), StringComparison.Ordinal);
    }

    [Fact]
    public void Message_id_form_and_hash_form_never_collide()
    {
        Assert.NotEqual(MessageKey.For(Message("<msg_a@example.test>")), MessageKey.For(Message(null)));
    }
}

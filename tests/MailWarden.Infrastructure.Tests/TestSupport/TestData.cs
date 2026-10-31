namespace MailWarden.Infrastructure.Tests.TestSupport;

/// <summary>Builders for artificial domain objects. All values are synthetic.</summary>
internal static class TestData
{
    /// <summary>Default message date: a non-UTC offset so offset loss is detectable.</summary>
    public static readonly DateTimeOffset DefaultDate = new(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(2));

    public static Account NewAccount(
        string displayName = "Account_A",
        string? emailAddress = null,
        MailProvider provider = MailProvider.GenericImap,
        Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            displayName,
            emailAddress ?? $"user_{Guid.NewGuid():N}@example.test",
            provider);

    public static MailFolder NewFolder(
        Guid accountId,
        string fullName = "INBOX",
        FolderRole role = FolderRole.Inbox,
        uint uidValidity = 1000) =>
        new(0, accountId, fullName, role, uidValidity);

    public static MailMessage NewMessage(
        Guid accountId,
        long folderId,
        uint uid,
        string subject = "sample_subject",
        string fromAddress = "sender_a@example.test",
        string fromName = "Sender A",
        DateTimeOffset? date = null,
        MessageFlags flags = MessageFlags.None,
        string? messageId = null,
        string? emlPath = null,
        string textPreview = "sample_preview") =>
        new(
            0,
            accountId,
            folderId,
            uid,
            messageId,
            subject,
            fromAddress,
            fromName,
            date ?? DefaultDate,
            flags,
            HasAttachments: false,
            Size: 1234,
            emlPath,
            textPreview);
}

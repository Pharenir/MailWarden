namespace MailWarden.Core.Model;

/// <summary>
/// A mail account. Credentials are not part of the model (they live in the credential store).
/// </summary>
/// <param name="Id">Stable account id, chosen by the caller.</param>
/// <param name="DisplayName">Name shown in the UI.</param>
/// <param name="EmailAddress">Primary address; unique across accounts (case-insensitive).</param>
/// <param name="Provider">Provider profile to use.</param>
public sealed record Account(Guid Id, string DisplayName, string EmailAddress, MailProvider Provider);

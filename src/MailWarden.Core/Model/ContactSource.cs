namespace MailWarden.Core.Model;

/// <summary>
/// Origin of an address book entry.
/// </summary>
public enum ContactSource
{
    /// <summary>Entered by the user.</summary>
    Manual,

    /// <summary>Imported from a vCard/CSV file or a provider.</summary>
    Imported,

    /// <summary>Collected automatically from sent mail.</summary>
    AutoCollected,
}

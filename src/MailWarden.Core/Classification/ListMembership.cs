namespace MailWarden.Core.Classification;

/// <summary>
/// Entry of a sender in the manual allow/block lists.
/// </summary>
public enum ListMembership
{
    /// <summary>Sender is on neither list.</summary>
    None,

    /// <summary>Sender (address or domain) is on the allow list.</summary>
    Allowed,

    /// <summary>Sender (address or domain) is on the block list.</summary>
    Blocked,
}

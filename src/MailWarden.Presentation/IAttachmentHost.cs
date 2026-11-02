namespace MailWarden.Presentation;

/// <summary>
/// What only the window and Windows can do for an attachment. The application implements it; nothing about how it is done
/// belongs into the other layers.
/// </summary>
public interface IAttachmentHost
{
    /// <summary>Shows the save dialog of Windows. Called on the UI thread.</summary>
    /// <param name="proposedFileName">The safe name the dialog proposes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully qualified path the user chose; null when the dialog was cancelled.</returns>
    Task<string?> PickSaveTargetAsync(string proposedFileName, CancellationToken ct = default);

    /// <summary>Starts the program Windows assigns to the file, with no arguments of ours. Called on the UI thread.</summary>
    /// <param name="fullPath">Full path of a prepared file.</param>
    /// <returns>False when Windows refused.</returns>
    Task<bool> OpenWithDefaultProgramAsync(string fullPath);
}

namespace MailWarden.Presentation;

/// <summary>
/// Runs an action on the UI thread.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues the action; returns without waiting for it.</summary>
    /// <param name="action">Action to run on the UI thread.</param>
    void Post(Action action);
}

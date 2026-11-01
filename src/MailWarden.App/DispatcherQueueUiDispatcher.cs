using MailWarden.Presentation;
using Microsoft.UI.Dispatching;

namespace MailWarden.App;

/// <summary>
/// <see cref="IUiDispatcher"/> over the dispatcher queue of the UI thread.
/// </summary>
/// <param name="queue">Dispatcher queue of the UI thread.</param>
internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    /// <inheritdoc />
    public void Post(Action action)
    {
        // False only while the application shuts down; a status update is not needed any more then.
        _ = queue.TryEnqueue(new DispatcherQueueHandler(action));
    }
}

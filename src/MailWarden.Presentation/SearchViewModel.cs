using MailWarden.Core.Text;

namespace MailWarden.Presentation;

/// <summary>
/// The local full-text search. Results appear in the message list like a view, with their account label; the navigation
/// selection stays. The search text goes to no log.
/// </summary>
public sealed class SearchViewModel : ObservableObject
{
    /// <summary>Largest number of results.</summary>
    public const int ResultLimit = 500;

    private readonly MessageListViewModel messages;
    private string query = string.Empty;
    private bool isSearching;
    private bool isActive;
    private int searchGeneration;
    private bool isClosed;

    internal SearchViewModel(MessageListViewModel messages)
    {
        this.messages = messages;
    }

    /// <summary>The search text; null counts as empty.</summary>
    public string Query
    {
        get => query;
        set => SetProperty(ref query, value ?? string.Empty);
    }

    /// <summary>True while the newest search runs.</summary>
    public bool IsSearching
    {
        get => isSearching;
        private set => SetProperty(ref isSearching, value);
    }

    /// <summary>True while the list shows search results instead of the selected view.</summary>
    public bool IsActive
    {
        get => isActive;
        private set => SetProperty(ref isActive, value);
    }

    /// <summary>
    /// Shows the results for <see cref="Query"/>, at most <see cref="ResultLimit"/>. An empty or blank text goes back to the
    /// previous view; a text the search cannot use gives an empty list. The newest call wins.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when this search ended or a newer one replaced it.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task SearchAsync(CancellationToken ct = default)
    {
        if (isClosed)
        {
            return;
        }

        int generation = ++searchGeneration;

        // Control characters alone count as blank: they are removed before anything is shown or searched for.
        if (string.IsNullOrWhiteSpace(SafeDisplayText.Clean(query)))
        {
            IsSearching = false;
            if (IsActive)
            {
                IsActive = false;
                await messages.ReturnToViewAsync(ct);
            }

            return;
        }

        IsActive = true;
        IsSearching = true;
        try
        {
            await messages.ShowSearchAsync(query, ct);
        }
        finally
        {
            if (generation == searchGeneration)
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>Forgets the search because another view was selected in the navigation.</summary>
    internal void Reset()
    {
        searchGeneration++;
        Query = string.Empty;
        IsSearching = false;
        IsActive = false;
    }

    /// <summary>Later searches do nothing.</summary>
    internal void Close()
    {
        isClosed = true;
    }
}

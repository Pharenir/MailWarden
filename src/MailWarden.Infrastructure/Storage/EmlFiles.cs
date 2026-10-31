using MailWarden.Core.Storage;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// EML file steps shared by the sync engine and the command executor.
/// </summary>
internal static class EmlFiles
{
    /// <summary>
    /// Deletes the given EML files one by one. A file that cannot be deleted is skipped (an orphan file is left for a
    /// later cleanup) so that the caller can still remove the rows; null paths are ignored; cancellation propagates.
    /// </summary>
    /// <param name="emlStore">Store of the raw messages.</param>
    /// <param name="emlPaths">Relative EML paths (null = the row has no file).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when every file was deleted or skipped.</returns>
    internal static async Task DeleteSkippingFailuresAsync(
        IEmlStore emlStore,
        IEnumerable<string?> emlPaths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(emlStore);
        ArgumentNullException.ThrowIfNull(emlPaths);
        foreach (string? emlPath in emlPaths)
        {
            if (emlPath is null)
            {
                continue;
            }

            try
            {
                await emlStore.DeleteAsync(emlPath, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Skipped on purpose, see summary.
            }
        }
    }
}

using System.Globalization;
using MailWarden.Core.Storage;

namespace MailWarden.Infrastructure.Storage;

/// <summary>
/// <see cref="IEmlStore"/> on the file system below <see cref="DataPaths.MailRoot"/>. Every relative path is
/// validated by <see cref="DataPaths.ResolveMailPath"/>. IO failures surface as <see cref="StorageException"/>.
/// </summary>
/// <param name="paths">Data paths of the running instance.</param>
public sealed class FileEmlStore(DataPaths paths) : IEmlStore
{
    private const int CopyBufferSize = 81920;

    private readonly DataPaths paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="folderId"/> or <paramref name="uid"/> is 0.</exception>
    public string RelativePathFor(Guid accountId, long folderId, uint uid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(folderId);
        ArgumentOutOfRangeException.ThrowIfZero(uid);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"accounts/{accountId:D}/folders/{folderId}/{uid}.eml");
    }

    /// <inheritdoc />
    public async Task WriteAsync(string relativePath, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        string targetPath = paths.ResolveMailPath(relativePath);
        string targetDirectory = Path.GetDirectoryName(targetPath)!;
        string temporaryPath = Path.Combine(
            targetDirectory,
            $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            // Write the complete file next to the target, flush it to disk, then replace the target in one move.
            Directory.CreateDirectory(targetDirectory);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = CopyBufferSize,
            };
            await using (var temporaryFile = new FileStream(temporaryPath, options))
            {
                await content.CopyToAsync(temporaryFile, CopyBufferSize, ct).ConfigureAwait(false);
                await temporaryFile.FlushAsync(ct).ConfigureAwait(false);
                temporaryFile.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageException($"Writing EML file '{relativePath}' failed: {exception.Message}", exception);
        }
        finally
        {
            // Covers failures and cancellation; after a successful move the temp file no longer exists.
            DeleteTemporaryFile(temporaryPath);
        }
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        string filePath = paths.ResolveMailPath(relativePath);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new EmlNotFoundException(relativePath);
        }

        try
        {
            // Delete sharing lets a later overwrite or delete succeed while a reader is still open.
            var options = new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = CopyBufferSize,
            };
            return Task.FromResult<Stream>(new FileStream(filePath, options));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new EmlNotFoundException(relativePath, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageException($"Opening EML file '{relativePath}' failed: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public bool Exists(string relativePath) => File.Exists(paths.ResolveMailPath(relativePath));

    /// <inheritdoc />
    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        string filePath = paths.ResolveMailPath(relativePath);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            return Task.CompletedTask;
        }

        try
        {
            File.Delete(filePath);
            return Task.CompletedTask;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StorageException($"Deleting EML file '{relativePath}' failed: {exception.Message}", exception);
        }
    }

    // Best effort by design: the original exception (or success) matters more than a leftover temp file,
    // which never shadows real data because of its unique name.
    private static void DeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
            // Ignored on purpose, see method comment.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignored on purpose, see method comment.
        }
    }
}

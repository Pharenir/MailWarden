using System.ComponentModel;
using System.Diagnostics;
using MailWarden.Infrastructure.Logging;
using MailWarden.Presentation;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MailWarden.App;

/// <summary>
/// The two things only Windows can do for an attachment: the save dialog and starting the assigned program. All knowledge about
/// how they work in an application without a package stays in this class. A failure of the dialog reaches the caller, which shows
/// it as a notice; nothing here may end the running application.
/// </summary>
/// <param name="log">Critical log; type names only, because the messages of these exceptions can name the file.</param>
internal sealed class WindowAttachmentHost(ICriticalLog log) : IAttachmentHost
{
    // The picker needs a file type; this one stands for "no extension".
    private const string NoFileType = ".";

    private const int MaxFileTypeCharacters = 32;

    // ERROR_CANCELLED: the user closed a question of Windows about the file. That is an answer, no refusal.
    private const int ErrorCancelled = 1223;

    private Window? ownerWindow;

    /// <summary>Names the window that owns the dialog; called once when it exists, because the shell is created before it.</summary>
    /// <param name="window">The main window.</param>
    public void AttachTo(Window window) => ownerWindow = window;

    /// <inheritdoc />
    public async Task<string?> PickSaveTargetAsync(string proposedFileName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Window window = ownerWindow ?? throw new InvalidOperationException("The owner window is not known yet.");
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = proposedFileName,
        };
        picker.FileTypeChoices.Add(UiText.AttachmentFileType, [FileTypeOf(proposedFileName)]);

        // Without a package the picker does not find its window by itself.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <inheritdoc />
    public Task<bool> OpenWithDefaultProgramAsync(string fullPath)
    {
        return Task.Run(() => StartAssignedProgram(fullPath));
    }

    // The picker refuses unusual file types, and the name stays as proposed under the type for no extension.
    private static string FileTypeOf(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        bool isUsable = extension.Length is > 1 and <= MaxFileTypeCharacters
            && extension.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        return isUsable ? extension : NoFileType;
    }

    // Only the path goes to the shell, as the file and never as a command line, and the arguments stay empty. The working directory is
    // the folder of this one file, so no program finds anything else beside it.
    private bool StartAssignedProgram(string fullPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty,
            };
            using Process? process = Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.Write(CriticalLogLevel.Critical, "WindowAttachmentHost.StartAssignedProgram", $"Starting a program failed: {exception.GetType().Name}");
            return false;
        }
    }
}

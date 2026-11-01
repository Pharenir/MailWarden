using System.Text;
using MailWarden.Infrastructure.Logging;
using MailWarden.Presentation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace MailWarden.App;

/// <summary>
/// Wires the browser control of the reading pane to <see cref="WebContentPolicy"/>: an InPrivate profile below the data
/// folder, no scripts, no downloads, every request through <c>WebResourceRequested</c>, the document from memory at
/// <see cref="WebContentPolicy.DocumentUri"/>. Links open in the system handler.
/// </summary>
internal sealed class MailWebViewHost
{
    private const string DocumentHeaders = "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store";

    private readonly WebView2 webView;
    private readonly string userDataFolder;
    private readonly ICriticalLog log;
    private CoreWebView2? core;
    private CoreWebView2Environment? environment;
    private byte[]? documentBytes;
    private string? documentHtml;
    private bool allowRemoteImages;
    private bool isInitializing;
    private bool isClosed;

    /// <summary>Creates the host. The browser starts with the first document.</summary>
    /// <param name="webView">The control of the window.</param>
    /// <param name="userDataFolder">Folder of the browser data, <c>&lt;data&gt;\webview2</c>.</param>
    /// <param name="log">Critical log.</param>
    public MailWebViewHost(WebView2 webView, string userDataFolder, ICriticalLog log)
    {
        this.webView = webView;
        this.userDataFolder = userDataFolder;
        this.log = log;
    }

    /// <summary>Raised when the browser cannot start; the document then cannot be shown.</summary>
    public event EventHandler? InitializationFailed;

    /// <summary>Shows a document; the same document again does nothing.</summary>
    /// <param name="html">Output of the reading document builder.</param>
    /// <param name="remoteImagesAllowed">True when the user approved remote images for this message.</param>
    public void Show(string html, bool remoteImagesAllowed)
    {
        if (isClosed || (html == documentHtml && remoteImagesAllowed == allowRemoteImages))
        {
            return;
        }

        documentHtml = html;
        documentBytes = Encoding.UTF8.GetBytes(html);
        allowRemoteImages = remoteImagesAllowed;
        if (core is not null)
        {
            core.Navigate(WebContentPolicy.DocumentUri);
        }
        else if (!isInitializing)
        {
            isInitializing = true;
            _ = InitializeAsync();
        }
    }

    /// <summary>
    /// Forgets the document; a later request for it is refused. Cookies of remote images end with their message.
    /// </summary>
    public void Clear()
    {
        if (documentHtml is not null && !isClosed)
        {
            DeleteCookies();
        }

        documentHtml = null;
        documentBytes = null;
        allowRemoteImages = false;
    }

    // Runs on every change of the message, also on error paths: a failure here must not end them.
    private void DeleteCookies()
    {
        try
        {
            core?.CookieManager.DeleteAllCookies();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.Write(CriticalLogLevel.Critical, "MailWebViewHost.DeleteCookies", $"Deleting the cookies failed: {exception.GetType().Name}");
        }
    }

    /// <summary>Ends the browser processes of this control.</summary>
    public void Close()
    {
        if (isClosed)
        {
            return;
        }

        Clear();
        isClosed = true;
        webView.Close();
    }

    private static void Launch(string uri, ICriticalLog log)
    {
        _ = LaunchAsync(uri, log);
    }

    private static async Task LaunchAsync(string uri, ICriticalLog log)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(WebContentPolicy.SanitizeMailto(uri)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.Write(CriticalLogLevel.Critical, "MailWebViewHost.LaunchAsync", $"Opening a link failed: {exception.GetType().Name}");
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            webView.DefaultBackgroundColor = Microsoft.UI.Colors.White;
            environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                string.Empty,
                userDataFolder,
                new CoreWebView2EnvironmentOptions());

            // InPrivate: cookies and caches of remote images do not outlive the session.
            CoreWebView2ControllerOptions controllerOptions = environment.CreateCoreWebView2ControllerOptions();
            controllerOptions.IsInPrivateModeEnabled = true;
            await webView.EnsureCoreWebView2Async(environment, controllerOptions);
            if (isClosed)
            {
                return;
            }

            core = webView.CoreWebView2;
            Configure(core);
            if (documentHtml is not null)
            {
                core.Navigate(WebContentPolicy.DocumentUri);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Closing the control during the start ends the start with an exception; that is no failure.
            if (!isClosed)
            {
                log.Write(CriticalLogLevel.Critical, "MailWebViewHost.InitializeAsync", $"Starting the browser failed: {exception.GetType().Name}");
                InitializationFailed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void Configure(CoreWebView2 browser)
    {
        CoreWebView2Settings settings = browser.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = true;

        // No online lookup of the message address; links are opened outside the control anyway.
        settings.IsReputationCheckingRequired = false;

        browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        browser.WebResourceRequested += OnWebResourceRequested;
        browser.NavigationStarting += OnNavigationStarting;
        browser.NewWindowRequested += OnNewWindowRequested;
        browser.DownloadStarting += OnDownloadStarting;
        browser.BasicAuthenticationRequested += OnBasicAuthenticationRequested;
        browser.PermissionRequested += OnPermissionRequested;
    }

    // Allowed remote images get no response here, so the browser loads them itself.
    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        string uri = args.Request.Uri;
        bool isImageRequest = args.ResourceContext == CoreWebView2WebResourceContext.Image;
        if (!WebContentPolicy.AllowRequest(uri, isImageRequest, allowRemoteImages)
            || (uri == WebContentPolicy.DocumentUri && documentBytes is null))
        {
            args.Response = environment!.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            return;
        }

        if (uri == WebContentPolicy.DocumentUri)
        {
            // From memory: NavigateToString has a limit of 2 MB.
            var content = new MemoryStream(documentBytes!, writable: false);
            args.Response = environment!.CreateWebResourceResponse(content.AsRandomAccessStream(), 200, "OK", DocumentHeaders);
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        NavigationDecision decision = WebContentPolicy.DecideNavigation(args.Uri, args.IsUserInitiated);
        if (decision == NavigationDecision.Allow)
        {
            return;
        }

        args.Cancel = true;
        if (decision == NavigationDecision.OpenExternally)
        {
            Launch(args.Uri, log);
        }
    }

    // Never a second browser window; an allowed target opens like a normal click.
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (WebContentPolicy.DecideNavigation(args.Uri, args.IsUserInitiated) == NavigationDecision.OpenExternally)
        {
            Launch(args.Uri, log);
        }
    }

    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
    }

    // A remote image must not be able to ask for a password inside the mail.
    private void OnBasicAuthenticationRequested(CoreWebView2 sender, CoreWebView2BasicAuthenticationRequestedEventArgs args)
    {
        args.Cancel = true;
    }

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
    }
}

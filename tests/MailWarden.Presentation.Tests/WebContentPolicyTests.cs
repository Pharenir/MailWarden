namespace MailWarden.Presentation.Tests;

/// <summary>AP-04a cases 18 and 19 (D4): <see cref="WebContentPolicy"/>. Pure, synchronous logic.</summary>
[Trait("Category", "feature:ui")]
[Trait("Category", "smoke")]
public sealed class WebContentPolicyTests
{
    // Case 18
    [Fact]
    public void Document_uri_constant_has_the_documented_value()
    {
        Assert.Equal("https://mail.mailwarden.invalid/message.html", WebContentPolicy.DocumentUri);
    }

    // Case 18
    [Fact]
    public void Document_uri_is_allowed()
    {
        Assert.Equal(NavigationDecision.Allow, WebContentPolicy.DecideNavigation(WebContentPolicy.DocumentUri));
    }

    // Case 18: "exact" - variants of the document address are ordinary external links, never Allow.
    [Theory]
    [InlineData("https://mail.mailwarden.invalid/message.html?x=1")]
    [InlineData("https://mail.mailwarden.invalid/other.html")]
    [InlineData("https://mail.mailwarden.invalid/message.html#fragment")]
    [InlineData("http://mail.mailwarden.invalid/message.html")]
    public void Variants_of_the_document_uri_are_not_allowed(string uri)
    {
        Assert.NotEqual(NavigationDecision.Allow, WebContentPolicy.DecideNavigation(uri));
    }

    // Case 18
    [Theory]
    [InlineData("http://example.test/page")]
    [InlineData("https://example.test/page?x=1#top")]
    [InlineData("HTTPS://Example.test/Upper")]
    [InlineData("mailto:recipient_b@example.test")]
    [InlineData("MAILTO:recipient_b@example.test?subject=hello")]
    public void Absolute_http_https_and_mailto_open_externally(string uri)
    {
        Assert.Equal(NavigationDecision.OpenExternally, WebContentPolicy.DecideNavigation(uri));
    }

    // Case 18
    [Theory]
    [InlineData("https://bank.test@evil.test/login")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("http://user@example.test/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("data:text/html;base64,PGI+eDwvYj4=")]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("about:blank")]
    [InlineData("ftp://example.test/file")]
    [InlineData("blob:https://example.test/0000")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData("/relative/path")]
    [InlineData("relative.html")]
    [InlineData("//example.test/protocol-relative")]
    [InlineData("#fragment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage ::: %%% not a uri")]
    [InlineData("http://")]
    public void Everything_else_is_blocked(string uri)
    {
        Assert.Equal(NavigationDecision.Block, WebContentPolicy.DecideNavigation(uri));
    }

    // Case 19
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Document_uri_request_is_always_allowed(bool isImageRequest, bool allowRemoteImages)
    {
        Assert.True(WebContentPolicy.AllowRequest(WebContentPolicy.DocumentUri, isImageRequest, allowRemoteImages));
    }

    // Case 19
    [Theory]
    [InlineData("https://tracker.test/pixel.gif")]
    [InlineData("http://tracker.test/pixel.gif")]
    public void Remote_image_request_is_allowed_only_when_images_are_allowed(string uri)
    {
        Assert.True(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: true));
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: false));
    }

    // Case 19
    [Theory]
    [InlineData("https://tracker.test/script.js")]
    [InlineData("http://tracker.test/style.css")]
    public void Remote_non_image_request_is_never_allowed(string uri)
    {
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: false, allowRemoteImages: true));
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: false, allowRemoteImages: false));
    }

    // Case 19
    [Theory]
    [InlineData("file:///c:/secret.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.test/a.png")]
    [InlineData("mailto:recipient_b@example.test")]
    [InlineData("/relative/a.png")]
    [InlineData("")]
    [InlineData("garbage ::: %%%")]
    public void Every_other_request_is_denied_even_for_images_with_remote_images_allowed(string uri)
    {
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: true));
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: false, allowRemoteImages: true));
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: false));
    }
}

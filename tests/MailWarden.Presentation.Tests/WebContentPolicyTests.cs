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

    // ---- Case 57 (K11) -------------------------------------------------------------------------------------

    // Case 57
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Document_uri_is_allowed_with_or_without_user_initiation(bool isUserInitiated)
    {
        Assert.Equal(NavigationDecision.Allow, WebContentPolicy.DecideNavigation(WebContentPolicy.DocumentUri, isUserInitiated));
    }

    // Case 57
    [Theory]
    [InlineData("http://example.test/page")]
    [InlineData("https://example.test/page?x=1")]
    [InlineData("mailto:recipient_b@example.test")]
    public void External_links_open_only_when_the_user_initiated_the_navigation(string uri)
    {
        Assert.Equal(NavigationDecision.OpenExternally, WebContentPolicy.DecideNavigation(uri, isUserInitiated: true));
        Assert.Equal(NavigationDecision.Block, WebContentPolicy.DecideNavigation(uri, isUserInitiated: false));
    }

    // Case 57
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<b>x</b>")]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("about:blank")]
    [InlineData("https://bank.test@evil.test/login")]
    [InlineData("/relative/path")]
    [InlineData("")]
    [InlineData("garbage ::: %%% not a uri")]
    public void Blocked_uris_stay_blocked_whatever_the_initiation(string uri)
    {
        Assert.Equal(NavigationDecision.Block, WebContentPolicy.DecideNavigation(uri, isUserInitiated: true));
        Assert.Equal(NavigationDecision.Block, WebContentPolicy.DecideNavigation(uri, isUserInitiated: false));
    }

    // Case 57: the one-argument overload keeps its meaning.
    [Fact]
    public void One_argument_overload_still_opens_external_links()
    {
        Assert.Equal(NavigationDecision.OpenExternally, WebContentPolicy.DecideNavigation("https://example.test/"));
    }

    // ---- Case 58 (K12) -------------------------------------------------------------------------------------

    private static string[] QueryKeys(string mailto)
    {
        int queryStart = mailto.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0 || queryStart == mailto.Length - 1)
        {
            return [];
        }

        return [.. mailto[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair => pair.Split('=')[0])];
    }

    private static string QueryValue(string mailto, string key)
    {
        string pair = mailto[(mailto.IndexOf('?', StringComparison.Ordinal) + 1)..]
            .Split('&')
            .First(candidate => string.Equals(candidate.Split('=')[0], key, StringComparison.OrdinalIgnoreCase));
        return Uri.UnescapeDataString(pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..]);
    }

    // Case 58
    [Fact]
    public void Sanitize_mailto_keeps_only_to_cc_bcc_subject_and_body()
    {
        string result = WebContentPolicy.SanitizeMailto(
            "mailto:recipient_b@example.test?to=recipient_c@example.test&cc=copy_d@example.test&bcc=hidden_e@example.test&subject=Hello&body=Text" +
            "&attach=C:\\secret.txt&from=other@example.test&in-reply-to=x&x-custom=1");

        string[] keys = QueryKeys(result);
        Assert.Equal(new[] { "bcc", "body", "cc", "subject", "to" }, keys.OrderBy(key => key, StringComparer.Ordinal).ToArray());
        Assert.StartsWith("mailto:recipient_b@example.test", result, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    // Case 58
    [Theory]
    [InlineData("attach")]
    [InlineData("ATTACH")]
    [InlineData("Attach")]
    [InlineData("attachment")]
    public void Sanitize_mailto_drops_attach_in_every_spelling(string key)
    {
        string result = WebContentPolicy.SanitizeMailto($"mailto:recipient_b@example.test?subject=Hi&{key}=C:\\secret.txt");

        Assert.Equal(new[] { "subject" }, QueryKeys(result));
        Assert.DoesNotContain("secret", result, StringComparison.OrdinalIgnoreCase);
    }

    // Case 58: names are compared case-insensitively, so allowed names in other spellings stay.
    [Fact]
    public void Sanitize_mailto_keeps_allowed_names_in_any_case()
    {
        string result = WebContentPolicy.SanitizeMailto("mailto:recipient_b@example.test?TO=a@example.test&Subject=Hi&CC=c@example.test&BODY=b&Bcc=d@example.test");

        Assert.Equal(5, QueryKeys(result).Length);
        Assert.Equal("Hi", QueryValue(result, "subject"));
    }

    // Case 58
    [Fact]
    public void Sanitize_mailto_keeps_the_meaning_of_encoded_values()
    {
        string result = WebContentPolicy.SanitizeMailto("mailto:recipient_b@example.test?subject=Hello%20World%20%C3%A4%26%3D&body=Line1%0D%0ALine2&attach=x");

        Assert.Equal("Hello World \u00E4&=", QueryValue(result, "subject"));
        Assert.Equal("Line1\r\nLine2", QueryValue(result, "body"));
        Assert.DoesNotContain("attach", result, StringComparison.OrdinalIgnoreCase);
    }

    // Case 58
    [Fact]
    public void Sanitize_mailto_without_parameters_is_unchanged()
    {
        Assert.Equal("mailto:recipient_b@example.test", WebContentPolicy.SanitizeMailto("mailto:recipient_b@example.test"));
    }

    // Case 58
    [Fact]
    public void Sanitize_mailto_with_only_forbidden_parameters_keeps_just_the_address()
    {
        string result = WebContentPolicy.SanitizeMailto("mailto:recipient_b@example.test?attach=C:\\secret.txt&ATTACH=D:\\more.txt");

        Assert.Equal("mailto:recipient_b@example.test", result.TrimEnd('?'));
        Assert.Empty(QueryKeys(result));
    }

    // Case 58 (planner): input that is not a mailto link comes back unchanged and the call does not throw.
    [Theory]
    [InlineData("https://example.test/?attach=x")]
    [InlineData("")]
    [InlineData("garbage ::: %%%")]
    public void Sanitize_mailto_of_something_else_returns_it_unchanged(string uri)
    {
        string result = string.Empty;

        Exception? failure = Record.Exception(() => result = WebContentPolicy.SanitizeMailto(uri));

        Assert.Null(failure);
        Assert.Equal(uri, result);
    }
    // ---- Case 59 (K13) -------------------------------------------------------------------------------------

    // Case 59
    [Theory]
    [InlineData("http://localhost/pixel.gif")]
    [InlineData("https://LOCALHOST:8443/pixel.gif")]
    [InlineData("http://127.0.0.1/pixel.gif")]
    [InlineData("http://127.1.2.3:8080/pixel.gif")]
    [InlineData("http://10.0.0.5/pixel.gif")]
    [InlineData("http://10.255.255.254/pixel.gif")]
    [InlineData("http://172.16.0.1/pixel.gif")]
    [InlineData("http://172.31.255.1/pixel.gif")]
    [InlineData("http://192.168.1.1/pixel.gif")]
    [InlineData("http://169.254.169.254/pixel.gif")]
    [InlineData("http://[::1]/pixel.gif")]
    [InlineData("http://[fe80::1]/pixel.gif")]
    [InlineData("http://[fd00::1]/pixel.gif")]
    [InlineData("http://[::ffff:127.0.0.1]/pixel.gif")]
    public void Local_and_private_addresses_are_denied_even_with_approval(string uri)
    {
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: true));
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: false));
    }

    // Case 59 positive control: public hosts and addresses next to the private ranges are allowed with approval.
    [Theory]
    [InlineData("https://example.test/pixel.gif")]
    [InlineData("http://8.8.8.8/pixel.gif")]
    [InlineData("https://172.15.255.255/pixel.gif")]
    [InlineData("https://172.32.0.1/pixel.gif")]
    [InlineData("https://11.0.0.1/pixel.gif")]
    [InlineData("https://192.169.0.1/pixel.gif")]
    [InlineData("https://169.253.0.1/pixel.gif")]
    [InlineData("https://localhost.example.test/pixel.gif")]
    [InlineData("https://[2606:4700::1111]/pixel.gif")]
    public void Public_hosts_are_allowed_with_approval_and_denied_without(string uri)
    {
        Assert.True(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: true));
        Assert.False(WebContentPolicy.AllowRequest(uri, isImageRequest: true, allowRemoteImages: false));
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

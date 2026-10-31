namespace MailWarden.Core.Tests.TestSupport;

/// <summary>Artificial Authentication-Results header values.</summary>
internal static class AuthHeaders
{
    public const string Gmail =
        "mx.google.com; dkim=pass header.i=@example.test header.s=s1 header.b=AbCd; spf=pass (google.com: domain of " +
        "bounce@example.test designates 192.0.2.1 as permitted sender) smtp.mailfrom=bounce@example.test; dmarc=pass " +
        "(p=REJECT sp=REJECT dis=NONE) header.from=example.test";

    public const string Outlook =
        "spf=pass (sender IP is 192.0.2.1) smtp.mailfrom=example.test; dkim=pass (signature was verified) " +
        "header.d=example.test;dmarc=pass action=none header.from=example.test;compauth=pass reason=100";

    /// <summary>DMARC, SPF and DKIM all pass for <paramref name="domain"/>.</summary>
    public static string AllPass(string domain) =>
        $"mx.google.com; dkim=pass header.i=@{domain} header.s=s1; spf=pass smtp.mailfrom=bounce@{domain}; " +
        $"dmarc=pass header.from={domain}";

    /// <summary>DMARC, SPF and DKIM all fail for <paramref name="domain"/>.</summary>
    public static string AllFail(string domain) =>
        $"mx.example.test; dmarc=fail header.from={domain}; spf=fail smtp.mailfrom=bounce@{domain}; " +
        $"dkim=fail header.d={domain}";
}

using System.Globalization;

namespace MailWarden.Core.Tests.Classification;

[Trait("Category", "feature:classifier")]
public sealed class AuthenticationResultsParserTests
{
    private static void AssertEmptyLike(AuthenticationResults parsed)
    {
        Assert.Null(parsed.Spf);
        Assert.Null(parsed.Dmarc);
        Assert.Empty(parsed.Dkim);
    }

    // Case 1
    [Fact]
    public void Parse_gmail_example()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(AuthHeaders.Gmail);

        Assert.Equal("mx.google.com", parsed.AuthServId);
        Assert.NotNull(parsed.Spf);
        Assert.Equal(AuthVerdict.Pass, parsed.Spf.Verdict);
        Assert.Equal("example.test", parsed.Spf.Domain);
        AuthMethodResult dkim = Assert.Single(parsed.Dkim);
        Assert.Equal(AuthVerdict.Pass, dkim.Verdict);
        Assert.Equal("example.test", dkim.Domain);
        Assert.NotNull(parsed.Dmarc);
        Assert.Equal(AuthVerdict.Pass, parsed.Dmarc.Verdict);
        Assert.Equal("example.test", parsed.Dmarc.Domain);
    }

    // Case 2
    [Fact]
    public void Parse_outlook_example_without_authserv_id_and_ignoring_compauth()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(AuthHeaders.Outlook);

        Assert.Null(parsed.AuthServId);
        Assert.NotNull(parsed.Spf);
        Assert.Equal(AuthVerdict.Pass, parsed.Spf.Verdict);
        Assert.Equal("example.test", parsed.Spf.Domain);
        AuthMethodResult dkim = Assert.Single(parsed.Dkim);
        Assert.Equal(AuthVerdict.Pass, dkim.Verdict);
        Assert.Equal("example.test", dkim.Domain);
        Assert.NotNull(parsed.Dmarc);
        Assert.Equal(AuthVerdict.Pass, parsed.Dmarc.Verdict);
        Assert.Equal("example.test", parsed.Dmarc.Domain);
    }

    // Case 3
    [Fact]
    public void Parse_tolerates_case_spacing_comments_quotes_and_folding()
    {
        const string header =
            "MX.Example.Test;   SPF=FAIL (domain of evil=x.test; designates y=z.test) smtp.mailfrom=Bounce@Example.TEST ;\r\n" +
            "\tDKIM=Pass reason=\"a;b=c.test\"\r\n    header.d=Signer.Example.Test;  DMARC=Fail (policy=reject; x=y.test) header.from=Example.Test";

        AuthenticationResults parsed = AuthenticationResultsParser.Parse(header);

        Assert.NotNull(parsed.Spf);
        Assert.Equal(AuthVerdict.Fail, parsed.Spf.Verdict);
        Assert.Equal("example.test", parsed.Spf.Domain);
        AuthMethodResult dkim = Assert.Single(parsed.Dkim);
        Assert.Equal(AuthVerdict.Pass, dkim.Verdict);
        Assert.Equal("signer.example.test", dkim.Domain);
        Assert.NotNull(parsed.Dmarc);
        Assert.Equal(AuthVerdict.Fail, parsed.Dmarc.Verdict);
        Assert.Equal("example.test", parsed.Dmarc.Domain);
    }

    [Fact]
    public void Parse_accepts_missing_space_after_semicolon()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test;spf=pass smtp.mailfrom=bounce@example.test;dkim=pass header.d=example.test;dmarc=pass header.from=example.test");

        Assert.Equal("mx.test", parsed.AuthServId);
        Assert.Equal(AuthVerdict.Pass, parsed.Spf?.Verdict);
        Assert.Equal(AuthVerdict.Pass, Assert.Single(parsed.Dkim).Verdict);
        Assert.Equal(AuthVerdict.Pass, parsed.Dmarc?.Verdict);
    }

    // Case 4
    [Fact]
    public void Parse_keeps_several_dkim_results_in_header_order()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test; dkim=pass header.d=example.test; dkim=fail header.d=other.test");

        Assert.Equal(2, parsed.Dkim.Count);
        Assert.Equal(AuthVerdict.Pass, parsed.Dkim[0].Verdict);
        Assert.Equal("example.test", parsed.Dkim[0].Domain);
        Assert.Equal(AuthVerdict.Fail, parsed.Dkim[1].Verdict);
        Assert.Equal("other.test", parsed.Dkim[1].Domain);
    }

    [Fact]
    public void Parse_dkim_domain_prefers_header_d_over_header_i()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test; dkim=pass header.i=@signer.test header.d=Example.Test");

        Assert.Equal("example.test", Assert.Single(parsed.Dkim).Domain);
    }

    [Theory]
    [InlineData("header.i=@sub.example.test", "sub.example.test")]
    [InlineData("header.i=user@sub.example.test", "sub.example.test")]
    public void Parse_dkim_domain_falls_back_to_header_i(string identity, string expectedDomain)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse($"mx.test; dkim=pass {identity}");

        Assert.Equal(expectedDomain, Assert.Single(parsed.Dkim).Domain);
    }

    [Theory]
    [InlineData("smtp.mailfrom=bounce@example.test", "example.test")]
    [InlineData("smtp.mailfrom=example.test", "example.test")]
    [InlineData("smtp.mailfrom=Bounce@Mail.Example.TEST", "mail.example.test")]
    public void Parse_spf_domain_is_the_address_domain(string mailFrom, string expectedDomain)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse($"mx.test; spf=pass {mailFrom}");

        Assert.Equal(expectedDomain, parsed.Spf?.Domain);
    }

    [Fact]
    public void Parse_domain_is_null_when_absent()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse("mx.test; spf=fail; dmarc=fail; dkim=fail");

        Assert.NotNull(parsed.Spf);
        Assert.Null(parsed.Spf.Domain);
        Assert.NotNull(parsed.Dmarc);
        Assert.Null(parsed.Dmarc.Domain);
        Assert.Null(Assert.Single(parsed.Dkim).Domain);
    }

    [Theory]
    [InlineData("pass", AuthVerdict.Pass)]
    [InlineData("fail", AuthVerdict.Fail)]
    [InlineData("softfail", AuthVerdict.SoftFail)]
    [InlineData("hardfail", AuthVerdict.Fail)]
    [InlineData("neutral", AuthVerdict.Neutral)]
    [InlineData("none", AuthVerdict.None)]
    [InlineData("temperror", AuthVerdict.TempError)]
    [InlineData("permerror", AuthVerdict.PermError)]
    [InlineData("policy", AuthVerdict.Policy)]
    [InlineData("PASS", AuthVerdict.Pass)]
    [InlineData("SoftFail", AuthVerdict.SoftFail)]
    public void Parse_maps_verdict_words(string word, AuthVerdict expected)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse($"mx.test; spf={word} smtp.mailfrom=example.test");

        Assert.Equal(expected, parsed.Spf?.Verdict);
    }

    // Case 6
    [Fact]
    public void Parse_unknown_verdict_word_maps_to_unknown()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse("mx.test; dkim=maybe header.d=example.test");

        Assert.Equal(AuthVerdict.Unknown, Assert.Single(parsed.Dkim).Verdict);
    }

    [Fact]
    public void Parse_ignores_unknown_methods()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test; arc=pass; bimi=pass; compauth=fail reason=000; spf=pass smtp.mailfrom=example.test");

        Assert.Equal(AuthVerdict.Pass, parsed.Spf?.Verdict);
        Assert.Empty(parsed.Dkim);
        Assert.Null(parsed.Dmarc);
    }

    // Case 5
    [Theory]
    [InlineData("%%%;;==")]
    [InlineData("")]
    [InlineData("none")]
    [InlineData(";")]
    [InlineData("=")]
    [InlineData("(")]
    [InlineData("\"")]
    [InlineData("mx.test; spf=pass (unterminated comment smtp.mailfrom=example.test")]
    [InlineData("mx.test; dkim=\"unterminated quote header.d=example.test")]
    [InlineData("spf= ; dkim==; dmarc=pass=pass")]
    [InlineData("\r\n\t \r\n")]
    public void Parse_never_throws_for_malformed_content(string header)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(header);

        Assert.NotNull(parsed);
        Assert.NotNull(parsed.Dkim);
    }

    // Case 5
    [Theory]
    [InlineData("%%%;;==")]
    [InlineData("")]
    [InlineData("none")]
    public void Parse_garbage_yields_no_method_results(string header)
    {
        AssertEmptyLike(AuthenticationResultsParser.Parse(header));
    }

    [Fact]
    public void Parse_empty_string_has_no_authserv_id()
    {
        Assert.Null(AuthenticationResultsParser.Parse("").AuthServId);
    }

    // Case 5
    [Fact]
    public void Parse_null_throws_argument_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => AuthenticationResultsParser.Parse(null!));
    }

    [Fact]
    public void Empty_has_no_results()
    {
        AuthenticationResults empty = AuthenticationResults.Empty;

        Assert.Null(empty.AuthServId);
        AssertEmptyLike(empty);
    }

    [Fact]
    public void Parse_uses_culture_invariant_comparison()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            AuthenticationResults parsed = AuthenticationResultsParser.Parse(
                "MX.TEST; SPF=PASS SMTP.MAILFROM=INFO@EXAMPLE.TEST; DKIM=PASS HEADER.D=EXAMPLE.TEST");

            Assert.Equal(AuthVerdict.Pass, parsed.Spf?.Verdict);
            Assert.Equal("example.test", parsed.Spf?.Domain);
            Assert.Equal("example.test", Assert.Single(parsed.Dkim).Domain);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

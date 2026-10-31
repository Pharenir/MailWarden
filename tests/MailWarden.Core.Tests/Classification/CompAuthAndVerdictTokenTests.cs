namespace MailWarden.Core.Tests.Classification;

/// <summary>AP-07b E1 (compauth) and E2 (verdict tokens bestguesspass / timeout): parser and AuthStatus behaviour.</summary>
[Trait("Category", "feature:classifier")]
public sealed class CompAuthAndVerdictTokenTests
{
    private const string Domain = TestInputs.PlausibleDomain;
    private const string Sender = "sender_a@" + Domain;

    /// <summary>Case 1 header: DMARC fails; the compauth part decides whether the provider's composite check passes.</summary>
    private static string DmarcFailCompAuth(string compAuthPart) =>
        $"mx.test; spf=none smtp.mailfrom={Domain}; dkim=none; dmarc=fail action=none header.from={Domain}; {compAuthPart}";

    private static readonly string DmarcFailCompAuthPass = DmarcFailCompAuth("compauth=pass reason=105");

    private static ClassificationResult Classify(string header, ISenderDirectory? directory = null) =>
        TestInputs.CreateClassifier(directory).Classify(TestInputs.Create(headers: [header]));

    // ---- parser: E1 ----------------------------------------------------------------------------------------

    // Case 1
    [Fact]
    public void Parse_reads_compauth_pass_and_ignores_the_reason_property()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(DmarcFailCompAuthPass);

        Assert.Equal(AuthVerdict.Pass, parsed.CompAuth);
        Assert.Equal(AuthVerdict.Fail, parsed.Dmarc?.Verdict);
    }

    [Theory]
    [InlineData("compauth=pass reason=105", AuthVerdict.Pass)]
    [InlineData("compauth=fail reason=000", AuthVerdict.Fail)]
    [InlineData("COMPAUTH=PASS reason=100", AuthVerdict.Pass)]
    [InlineData("compauth=softfail reason=201", AuthVerdict.SoftFail)]
    [InlineData("compauth=none reason=001", AuthVerdict.None)]
    [InlineData("compauth=pass", AuthVerdict.Pass)]
    public void Parse_maps_compauth_verdicts(string compAuth, AuthVerdict expected)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse($"mx.test; spf=none; {compAuth}");

        Assert.Equal(expected, parsed.CompAuth);
    }

    [Fact]
    public void Parse_compauth_is_none_when_absent()
    {
        Assert.Equal(AuthVerdict.None, AuthenticationResultsParser.Parse(AuthHeaders.Gmail).CompAuth);
        Assert.Equal(AuthVerdict.None, AuthenticationResultsParser.Parse("").CompAuth);
        Assert.Equal(AuthVerdict.None, AuthenticationResults.Empty.CompAuth);
    }

    [Fact]
    public void Parse_outlook_example_reads_compauth_pass()
    {
        Assert.Equal(AuthVerdict.Pass, AuthenticationResultsParser.Parse(AuthHeaders.Outlook).CompAuth);
    }

    // Case 5
    [Fact]
    public void Parse_first_compauth_result_counts_pass_before_fail()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test; compauth=pass reason=100; compauth=fail reason=000");

        Assert.Equal(AuthVerdict.Pass, parsed.CompAuth);
    }

    // Case 5 (reverse direction)
    [Fact]
    public void Parse_first_compauth_result_counts_fail_before_pass()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test; compauth=fail reason=000; compauth=pass reason=100");

        Assert.Equal(AuthVerdict.Fail, parsed.CompAuth);
    }

    [Fact]
    public void Parse_compauth_inside_a_comment_or_value_does_not_count()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            "mx.test; spf=fail (compauth=pass reason=100) smtp.mailfrom=x=compauth=pass@example.test");

        Assert.Equal(AuthVerdict.None, parsed.CompAuth);
    }

    // ---- parser: E2 ----------------------------------------------------------------------------------------

    // Case 6, 7: mapping applies to every method
    [Theory]
    [InlineData("bestguesspass", AuthVerdict.None)]
    [InlineData("BestGuessPass", AuthVerdict.None)]
    [InlineData("timeout", AuthVerdict.TempError)]
    [InlineData("TIMEOUT", AuthVerdict.TempError)]
    public void Parse_maps_new_verdict_tokens_for_every_method(string token, AuthVerdict expected)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse(
            $"mx.test; spf={token} smtp.mailfrom=example.test; dkim={token} header.d=example.test; " +
            $"dmarc={token} header.from=example.test; compauth={token} reason=109");

        Assert.Equal(expected, parsed.Spf?.Verdict);
        Assert.Equal(expected, Assert.Single(parsed.Dkim).Verdict);
        Assert.Equal(expected, parsed.Dmarc?.Verdict);
        Assert.Equal(expected, parsed.CompAuth);
    }

    // Case 7
    [Fact]
    public void Parse_dkim_timeout_is_a_temp_error_with_domain()
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse($"mx.test; dkim=timeout header.d={Domain}");

        AuthMethodResult dkim = Assert.Single(parsed.Dkim);
        Assert.Equal(AuthVerdict.TempError, dkim.Verdict);
        Assert.Equal(Domain, dkim.Domain);
    }

    [Theory]
    [InlineData("pass", AuthVerdict.Pass)]
    [InlineData("fail", AuthVerdict.Fail)]
    [InlineData("softfail", AuthVerdict.SoftFail)]
    [InlineData("neutral", AuthVerdict.Neutral)]
    [InlineData("none", AuthVerdict.None)]
    [InlineData("temperror", AuthVerdict.TempError)]
    [InlineData("permerror", AuthVerdict.PermError)]
    [InlineData("policy", AuthVerdict.Policy)]
    [InlineData("maybe", AuthVerdict.Unknown)]
    public void Parse_other_verdict_tokens_are_unchanged(string token, AuthVerdict expected)
    {
        AuthenticationResults parsed = AuthenticationResultsParser.Parse($"mx.test; dmarc={token} header.from=example.test");

        Assert.Equal(expected, parsed.Dmarc?.Verdict);
    }

    // Case 6
    [Fact]
    public void Dmarc_bestguesspass_with_aligned_spf_pass_is_authenticated_via_spf()
    {
        ClassificationResult result = Classify(
            $"mx.test; spf=pass smtp.mailfrom={Domain}; dkim=none; dmarc=bestguesspass header.from={Domain}; compauth=pass reason=109");

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(-20, TestInputs.Find(result, "authentication")?.Points);
    }

    [Fact]
    public void Dmarc_bestguesspass_alone_is_not_authenticated()
    {
        ClassificationResult result = Classify($"mx.test; spf=none; dkim=none; dmarc=bestguesspass header.from={Domain}");

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "authentication"));
    }

    // Case 7
    [Fact]
    public void Dkim_timeout_alone_is_unknown_without_authentication_finding()
    {
        ClassificationResult result = Classify($"mx.test; dkim=timeout header.d={Domain}");

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "authentication"));
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Dkim_timeout_does_not_prevent_authentication_by_dmarc_pass()
    {
        ClassificationResult result = Classify($"mx.test; dkim=timeout header.d={Domain}; dmarc=pass header.from={Domain}");

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
    }

    // ---- evaluator and signal: E1 --------------------------------------------------------------------------

    // Case 1
    [Fact]
    public void Dmarc_fail_with_compauth_pass_is_unknown_with_zero_point_finding()
    {
        ClassificationResult result = Classify(DmarcFailCompAuthPass);

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        SignalResult finding = Assert.IsType<SignalResult>(TestInputs.Find(result, "authentication"));
        Assert.Equal(0, finding.Points);
        Assert.Contains("DMARC", finding.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            new[] { "composite", "compauth", "provider" }.Any(
                word => finding.Reason.Contains(word, StringComparison.OrdinalIgnoreCase)),
            $"Reason '{finding.Reason}' does not mention the provider's composite authentication");
        Assert.Equal(0, result.Score);
    }

    // Case 1: neither SPF-fail nor DKIM-fail points are added in the E1 case
    [Fact]
    public void Compauth_pass_with_dmarc_spf_and_dkim_fail_adds_no_points()
    {
        ClassificationResult result = Classify(
            $"mx.test; spf=fail smtp.mailfrom={Domain}; dkim=fail header.d={Domain}; dmarc=fail header.from={Domain}; compauth=pass reason=105");

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(0, TestInputs.Find(result, "authentication")?.Points);
        Assert.Equal(0, result.Score);
    }

    // Case 1: DMARC fail + compauth pass is Unknown even with an aligned SPF pass
    [Fact]
    public void Dmarc_fail_with_compauth_pass_and_aligned_spf_pass_is_unknown()
    {
        ClassificationResult result = Classify(
            $"mx.test; spf=pass smtp.mailfrom={Domain}; dmarc=fail header.from={Domain}; compauth=pass reason=105");

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(0, result.Score);
    }

    // Case 2
    [Fact]
    public void Dmarc_fail_with_compauth_fail_stays_failed_with_forty_points()
    {
        ClassificationResult result = Classify(DmarcFailCompAuth("compauth=fail reason=000"));

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.Equal(40, TestInputs.Find(result, "authentication")?.Points);
        Assert.Equal(40, result.Score);
    }

    [Theory]
    [InlineData("compauth=softfail reason=201")]
    [InlineData("compauth=none reason=001")]
    [InlineData("compauth=temperror")]
    [InlineData("")]
    public void Dmarc_fail_without_compauth_pass_stays_failed(string compAuthPart)
    {
        ClassificationResult result = Classify($"mx.test; dmarc=fail header.from={Domain}; {compAuthPart}");

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.Equal(40, TestInputs.Find(result, "authentication")?.Points);
    }

    // Case 5 through the classifier: the first compauth result counts
    [Fact]
    public void Two_compauth_results_use_the_first_for_the_status()
    {
        ClassificationResult passFirst = Classify(
            $"mx.test; dmarc=fail header.from={Domain}; compauth=pass reason=105; compauth=fail reason=000");
        ClassificationResult failFirst = Classify(
            $"mx.test; dmarc=fail header.from={Domain}; compauth=fail reason=000; compauth=pass reason=105");

        Assert.Equal(AuthStatus.Unknown, passFirst.AuthStatus);
        Assert.Equal(AuthStatus.Failed, failFirst.AuthStatus);
    }

    // Case 4
    [Fact]
    public void Dmarc_pass_with_compauth_pass_is_authenticated_as_before()
    {
        ClassificationResult result = Classify(
            $"mx.test; spf=pass smtp.mailfrom={Domain}; dkim=pass header.d={Domain}; dmarc=pass action=none header.from={Domain}; compauth=pass reason=100");

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
        Assert.Equal(-20, TestInputs.Find(result, "authentication")?.Points);
    }

    [Fact]
    public void Compauth_pass_alone_does_not_authenticate()
    {
        ClassificationResult result = Classify("mx.test; compauth=pass reason=100");

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Null(TestInputs.Find(result, "authentication"));
    }

    [Fact]
    public void Dmarc_pass_with_compauth_fail_stays_authenticated()
    {
        ClassificationResult result = Classify($"mx.test; dmarc=pass header.from={Domain}; compauth=fail reason=000");

        Assert.Equal(AuthStatus.Authenticated, result.AuthStatus);
    }

    // Only the top-most header counts (AP-07a), also for compauth
    [Fact]
    public void Compauth_of_a_lower_header_is_ignored()
    {
        ClassificationInput input = TestInputs.Create(
            headers: [$"mx.test; dmarc=fail header.from={Domain}", DmarcFailCompAuthPass]);

        Assert.Equal(AuthStatus.Failed, TestInputs.CreateClassifier().Classify(input).AuthStatus);
    }

    // Case 3
    [Fact]
    public void Contact_in_the_compauth_case_is_likely_legit_with_unverified_override()
    {
        var directory = new FakeSenderDirectory().AddContact(Sender);

        ClassificationResult result = Classify(DmarcFailCompAuthPass, directory);

        Assert.Equal(AuthStatus.Unknown, result.AuthStatus);
        Assert.Equal(SpamTier.LikelyLegit, result.Tier);
        Assert.Contains(
            "Sender is a contact but authentication could not be verified", result.Override, StringComparison.OrdinalIgnoreCase);
    }

    // Case 3 (positive control: with compauth=fail the contact is not rescued)
    [Fact]
    public void Contact_with_dmarc_fail_and_compauth_fail_is_not_trusted()
    {
        var directory = new FakeSenderDirectory().AddContact(Sender);

        ClassificationResult result = Classify(DmarcFailCompAuth("compauth=fail reason=000"), directory);

        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.NotEqual(SpamTier.TrustedContact, result.Tier);
        Assert.Null(result.Override);
        Assert.Equal(SpamTier.LikelySpam, result.Tier);
    }

    // Case 21 (positive control, E1 does not weaken the obvious-spam case)
    [Fact]
    public void Obvious_spam_with_failing_authentication_stays_certain_spam()
    {
        ClassificationResult result = TestInputs.CreateClassifier().Classify(TestInputs.ObviousSpam());

        Assert.Equal(SpamTier.CertainSpam, result.Tier);
        Assert.Equal(AuthStatus.Failed, result.AuthStatus);
        Assert.Equal(120, result.Score);
    }
}

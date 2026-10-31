using MailWarden.Core.Classification.Signals;

namespace MailWarden.Core.Classification;

/// <summary>
/// Four-level spam classifier: evaluates all signals, sums their points and applies the block/allow/contact rules.
/// Pure and deterministic; thread-safe as long as the signals and the sender directory are.
/// </summary>
public sealed class SpamClassifier
{
    private const string BlockListOverride = "Sender is on the block list";
    private const string AllowListOverride = "Sender is on the allow list";
    private const string UnverifiedContactOverride = "Sender is a contact but authentication could not be verified";

    private readonly ClassifierSettings settings;
    private readonly ISenderDirectory senderDirectory;
    private readonly ISpamSignal[] signals;

    /// <summary>Creates a classifier. The settings are validated here (fail fast).</summary>
    /// <param name="settings">Thresholds and lists.</param>
    /// <param name="senderDirectory">Address book and allow/block list lookups.</param>
    /// <param name="signals">Signals, evaluated in the given order.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The settings are invalid, a signal is null or has an empty id, or two
    /// signals share an id (compared case-insensitively).</exception>
    public SpamClassifier(ClassifierSettings settings, ISenderDirectory senderDirectory, IEnumerable<ISpamSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(senderDirectory);
        ArgumentNullException.ThrowIfNull(signals);
        settings.Validate();

        var signalArray = signals.ToArray();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in signalArray)
        {
            if (signal is null)
            {
                throw new ArgumentException("Signal list must not contain null entries.", nameof(signals));
            }

            if (string.IsNullOrWhiteSpace(signal.Id))
            {
                throw new ArgumentException("Every signal needs a non-empty id.", nameof(signals));
            }

            if (!seenIds.Add(signal.Id))
            {
                throw new ArgumentException($"Duplicate signal id '{signal.Id}'.", nameof(signals));
            }
        }

        this.settings = settings;
        this.senderDirectory = senderDirectory;
        this.signals = signalArray;
    }

    /// <summary>
    /// The built-in signals in this order: authentication, random-domain, suspicious-tld, domain-not-in-body,
    /// display-name-mismatch, reply-to-mismatch, link-domain-mismatch.
    /// </summary>
    /// <returns>New signal instances in a read-only list.</returns>
    public static IReadOnlyList<ISpamSignal> CreateDefaultSignals()
    {
        ISpamSignal[] defaultSignals =
        [
            new AuthenticationSignal(),
            new RandomDomainSignal(),
            new SuspiciousTldSignal(),
            new DomainNotInBodySignal(),
            new DisplayNameMismatchSignal(),
            new ReplyToMismatchSignal(),
            new LinkDomainMismatchSignal(),
        ];
        return Array.AsReadOnly(defaultSignals);
    }

    /// <summary>
    /// Classifies one message: build the context, evaluate all signals (always, also when an override decides),
    /// then block list → allow list (unless authentication failed) → authenticated contact → unverified contact
    /// (at most LikelyLegit) → score thresholds.
    /// </summary>
    /// <param name="input">Extracted message data.</param>
    /// <returns>The decision with all findings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="ArgumentException">FromAddress has no '@' or no domain, or a required member of
    /// <paramref name="input"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A signal returned a result whose SignalId differs from its Id
    /// (ordinal) or whose Reason is empty/whitespace (no silent points); the message names the signal id.</exception>
    public ClassificationResult Classify(ClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var context = CreateContext(input);

        // All signals, in order; Score is the plain sum of the findings.
        var findings = new List<SignalResult>();
        var score = 0;
        foreach (var signal in signals)
        {
            var finding = signal.Evaluate(input, context);
            if (finding is not null)
            {
                EnsureValidFinding(signal, finding);
                findings.Add(finding);
                score = checked(score + finding.Points);
            }
        }

        var (tier, overrideReason) = Decide(input.FromAddress, context, score);
        return new ClassificationResult(tier, score, context.AuthStatus, findings.AsReadOnly(), overrideReason);
    }

    // Project rule "no silent points": every finding carries its own signal id and a reason. Custom signals are
    // checked here, fail fast. Messages name only the signal id (code-defined), never message data.
    private static void EnsureValidFinding(ISpamSignal signal, SignalResult finding)
    {
        if (!string.Equals(finding.SignalId, signal.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Signal '{signal.Id}' returned a result with a different SignalId.");
        }

        if (string.IsNullOrWhiteSpace(finding.Reason))
        {
            throw new InvalidOperationException($"Signal '{signal.Id}' returned a result without a reason.");
        }
    }

    // Step 1 of the decision order: precomputed per-message values.
    private ClassificationContext CreateContext(ClassificationInput input)
    {
        if (input.FromAddress is null || input.FromName is null || input.AuthenticationResultsHeaders is null
            || input.BodyText is null || input.LinkHosts is null)
        {
            throw new ArgumentException(
                "FromAddress, FromName, AuthenticationResultsHeaders, BodyText and LinkHosts must not be null.",
                nameof(input));
        }

        var fromDomain = DomainNames.TryGetDomain(input.FromAddress)
            ?? throw new ArgumentException("FromAddress must contain '@' followed by a domain.", nameof(input));
        var fromOrganizationalDomain = DomainNames.GetOrganizationalDomain(fromDomain);

        // Only the top-most header is trustworthy (prepended by the receiving provider).
        var authentication = AuthenticationResults.Empty;
        if (input.AuthenticationResultsHeaders.Count > 0)
        {
            var topMostHeader = input.AuthenticationResultsHeaders[0]
                ?? throw new ArgumentException("AuthenticationResultsHeaders must not contain null entries.", nameof(input));
            authentication = AuthenticationResultsParser.Parse(topMostHeader);
        }

        var authStatus = AuthStatusEvaluator.Evaluate(authentication, fromDomain);
        var fromIsFreemail = settings.IsFreemailDomain(fromDomain) || settings.IsFreemailDomain(fromOrganizationalDomain);
        return new ClassificationContext(
            fromDomain, fromOrganizationalDomain, authentication, authStatus, fromIsFreemail, settings);
    }

    // Steps 3–7 of the decision order.
    private (SpamTier Tier, string? OverrideReason) Decide(string fromAddress, ClassificationContext context, int score)
    {
        var membership = senderDirectory.GetListMembership(fromAddress);
        if (membership == ListMembership.Blocked)
        {
            return (SpamTier.CertainSpam, BlockListOverride);
        }

        if (membership == ListMembership.Allowed && context.AuthStatus != AuthStatus.Failed)
        {
            return (SpamTier.TrustedContact, AllowListOverride);
        }

        var isContact = senderDirectory.IsContact(fromAddress);
        if (isContact && context.AuthStatus == AuthStatus.Authenticated)
        {
            var passDescription = AuthStatusEvaluator.DescribePass(context.Authentication, context.FromDomain);
            return (SpamTier.TrustedContact, $"Sender is a contact and authenticated ({passDescription})");
        }

        // Contact with unverifiable authentication: always LikelyLegit, never better or worse (C8).
        if (isContact && context.AuthStatus == AuthStatus.Unknown)
        {
            return (SpamTier.LikelyLegit, UnverifiedContactOverride);
        }

        // Contacts and allow-listed senders with failed authentication end up here, without override.
        return (GetScoreTier(score), null);
    }

    private SpamTier GetScoreTier(int score)
    {
        if (score >= settings.CertainSpamThreshold)
        {
            return SpamTier.CertainSpam;
        }

        return score >= settings.LikelySpamThreshold ? SpamTier.LikelySpam : SpamTier.LikelyLegit;
    }
}

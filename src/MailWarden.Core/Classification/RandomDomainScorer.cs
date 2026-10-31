using System.Collections.Frozen;

namespace MailWarden.Core.Classification;

/// <summary>
/// Heuristic "does this domain label look machine-generated?" score from four features of the lower-cased label:
/// <list type="bullet">
/// <item>vowel deficit: <c>clamp((0.30 - vowels/letters) / 0.30)</c>, 'y' and accented vowels count as vowels;</item>
/// <item>consonant run: <c>clamp((longest consonant run - 3) / 4)</c>, runs are broken by vowels, digits and '-';</item>
/// <item>rare bigrams: share of adjacent letter pairs that are two consonants not in a built-in table of common
/// English/German consonant clusters;</item>
/// <item>digit/letter alternation: 0 for fewer than 3 letter↔digit changes, else
/// <c>clamp((changes / (letters and digits - 1) - 0.4) / 0.4)</c>.</item>
/// </list>
/// Score = <c>max(alternation, 0.4 · vowel deficit + 0.3 · consonant run + 0.3 · rare bigrams)</c>, in [0, 1].
/// Pure arithmetic on invariant lower-case characters: deterministic and culture-invariant.
/// </summary>
public static class RandomDomainScorer
{
    private const int MinimumLength = 5;
    private const double VowelRatioLimit = 0.30;
    private const int TolerableConsonantRun = 3;
    private const int ConsonantRunSpan = 4;
    private const double AlternationRatioFloor = 0.4;
    private const double AlternationRatioSpan = 0.4;
    private const int MinimumAlternations = 3;
    private const double VowelWeight = 0.4;
    private const double ConsonantRunWeight = 0.3;
    private const double RareBigramWeight = 0.3;

    private const string Vowels = "aeiouyäöüàáâãåæèéêëìíîïòóôõøùúûýÿ";

    // Consonant pairs that are common in English and German words; any other consonant pair counts as rare.
    private static readonly FrozenSet<string> CommonConsonantPairs = new[]
    {
        "bb", "bl", "br", "bs", "bt",
        "ch", "ck", "cl", "cr", "ct",
        "dd", "dg", "dl", "dm", "dn", "dr", "ds", "dt", "dw",
        "ff", "fl", "fr", "fs", "ft",
        "gg", "gh", "gl", "gn", "gr", "gs", "gt",
        "hl", "hm", "hn", "hr", "hs", "ht",
        "kk", "kl", "kn", "kr", "ks", "kt",
        "lb", "ld", "lf", "lg", "lk", "ll", "lm", "ln", "lp", "ls", "lt", "lv", "lz",
        "mb", "mm", "mp", "ms", "mt",
        "nc", "nd", "nf", "ng", "nk", "nl", "nn", "ns", "nt", "nz",
        "pf", "ph", "pl", "pp", "pr", "ps", "pt",
        "rb", "rc", "rd", "rf", "rg", "rk", "rl", "rm", "rn", "rp", "rr", "rs", "rt", "rv", "rw", "rz",
        "sc", "sh", "sk", "sl", "sm", "sn", "sp", "ss", "st", "sw",
        "th", "tl", "tr", "ts", "tt", "tw", "tz",
        "wh", "wr",
        "xt",
        "zt", "zw", "zz",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Scores a registrable domain label from 0.0 (natural word) to 1.0 (random). Labels shorter than 5 characters
    /// (after removing '-') score 0.0.
    /// </summary>
    /// <param name="registrableLabel">Label to score, e.g. "qzxkvb73".</param>
    /// <returns>Score in [0, 1].</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registrableLabel"/> is null.</exception>
    public static double Score(string registrableLabel)
    {
        ArgumentNullException.ThrowIfNull(registrableLabel);

        var label = registrableLabel.ToLowerInvariant();
        var lengthWithoutHyphens = label.Length - label.Count(character => character == '-');
        if (lengthWithoutHyphens < MinimumLength)
        {
            return 0.0;
        }

        var features = MeasureFeatures(label);
        var pronounceabilityScore = (VowelWeight * VowelDeficit(features))
            + (ConsonantRunWeight * ConsonantRunExcess(features))
            + (RareBigramWeight * RareBigramShare(features));
        return Clamp(Math.Max(AlternationScore(features), pronounceabilityScore));
    }

    // Raw counts collected in one pass over the label.
    private readonly record struct LabelFeatures(
        int LetterCount,
        int VowelCount,
        int LongestConsonantRun,
        int LetterBigramCount,
        int RareBigramCount,
        int AlphanumericCount,
        int DigitLetterAlternations);

    private static LabelFeatures MeasureFeatures(string label)
    {
        int letterCount = 0, vowelCount = 0, consonantRun = 0, longestConsonantRun = 0;
        int letterBigramCount = 0, rareBigramCount = 0, alphanumericCount = 0, alternations = 0;

        for (var index = 0; index < label.Length; index++)
        {
            var character = label[index];
            var isLetter = char.IsLetter(character);
            var isDigit = char.IsDigit(character);
            if (isLetter || isDigit)
            {
                alphanumericCount++;
            }

            // Consonant runs and vowel ratio over letters only; anything else breaks a run.
            if (isLetter && IsVowel(character))
            {
                letterCount++;
                vowelCount++;
                consonantRun = 0;
            }
            else if (isLetter)
            {
                letterCount++;
                consonantRun++;
                longestConsonantRun = Math.Max(longestConsonantRun, consonantRun);
            }
            else
            {
                consonantRun = 0;
            }

            if (index == 0)
            {
                continue;
            }

            // Pairs of directly adjacent characters.
            var previous = label[index - 1];
            if (isLetter && char.IsLetter(previous))
            {
                letterBigramCount++;
                if (!IsVowel(previous) && !IsVowel(character) && !CommonConsonantPairs.Contains(string.Concat(previous, character)))
                {
                    rareBigramCount++;
                }
            }
            else if ((isLetter && char.IsDigit(previous)) || (isDigit && char.IsLetter(previous)))
            {
                alternations++;
            }
        }

        return new LabelFeatures(
            letterCount, vowelCount, longestConsonantRun, letterBigramCount, rareBigramCount, alphanumericCount, alternations);
    }

    // Labels without letters cannot be judged for pronounceability.
    private static double VowelDeficit(LabelFeatures features)
    {
        if (features.LetterCount == 0)
        {
            return 0.0;
        }

        var vowelRatio = (double)features.VowelCount / features.LetterCount;
        return Clamp((VowelRatioLimit - vowelRatio) / VowelRatioLimit);
    }

    private static double ConsonantRunExcess(LabelFeatures features)
    {
        return Clamp((double)(features.LongestConsonantRun - TolerableConsonantRun) / ConsonantRunSpan);
    }

    private static double RareBigramShare(LabelFeatures features)
    {
        return features.LetterBigramCount == 0 ? 0.0 : (double)features.RareBigramCount / features.LetterBigramCount;
    }

    private static double AlternationScore(LabelFeatures features)
    {
        if (features.DigitLetterAlternations < MinimumAlternations || features.AlphanumericCount < 2)
        {
            return 0.0;
        }

        var alternationRatio = (double)features.DigitLetterAlternations / (features.AlphanumericCount - 1);
        return Clamp((alternationRatio - AlternationRatioFloor) / AlternationRatioSpan);
    }

    private static bool IsVowel(char character) => Vowels.Contains(character, StringComparison.Ordinal);

    private static double Clamp(double value) => Math.Clamp(value, 0.0, 1.0);
}

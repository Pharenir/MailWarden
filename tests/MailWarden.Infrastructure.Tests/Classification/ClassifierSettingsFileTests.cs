using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07b E4: loading <see cref="ClassifierSettings"/> from <c>classifier-settings.json</c>.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassifierSettingsFileTests : IDisposable
{
    // Pure, synchronous file IO: no per-test timeout needed (xUnit v2 cannot interrupt sync code).
    private readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private string Write(string content, string fileName = ClassifierSettingsFile.FileName)
    {
        string path = Path.Combine(directory.RootPath, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static void AssertSameAsDefault(ClassifierSettings actual)
    {
        ClassifierSettings expected = ClassifierSettings.Default;
        Assert.Equal(expected.LikelySpamThreshold, actual.LikelySpamThreshold);
        Assert.Equal(expected.CertainSpamThreshold, actual.CertainSpamThreshold);
        AssertSameTlds(expected.SuspiciousTlds, actual);
        AssertSameFreemail(expected.FreemailDomains, actual);
    }

    private static void AssertSameTlds(IEnumerable<string> expected, ClassifierSettings actual) =>
        Assert.Equal(expected.OrderBy(entry => entry, StringComparer.Ordinal), actual.SuspiciousTlds.OrderBy(entry => entry, StringComparer.Ordinal));

    private static void AssertSameFreemail(IEnumerable<string> expected, ClassifierSettings actual) =>
        Assert.Equal(expected.OrderBy(entry => entry, StringComparer.Ordinal), actual.FreemailDomains.OrderBy(entry => entry, StringComparer.Ordinal));

    /// <summary>Asserts the contract for load errors: type, file name present, directory absent.</summary>
    private InvalidDataException AssertLoadFails(string content, string? expectedKey = null, string fileName = ClassifierSettingsFile.FileName)
    {
        string path = Write(content, fileName);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ClassifierSettingsFile.Load(path));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFileName(directory.RootPath), exception.Message, StringComparison.OrdinalIgnoreCase);
        if (expectedKey is not null)
        {
            Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
        }

        return exception;
    }

    [Fact]
    public void FileName_is_classifier_settings_json()
    {
        Assert.Equal("classifier-settings.json", ClassifierSettingsFile.FileName);
    }

    // Case 13
    [Fact]
    public void Missing_file_yields_the_default_settings()
    {
        ClassifierSettings settings = ClassifierSettingsFile.Load(Path.Combine(directory.RootPath, ClassifierSettingsFile.FileName));

        AssertSameAsDefault(settings);
    }

    [Fact]
    public void Empty_object_keeps_all_defaults()
    {
        AssertSameAsDefault(ClassifierSettingsFile.Load(Write("{}")));
    }

    // Case 14
    [Fact]
    public void Single_threshold_key_overrides_only_that_value()
    {
        ClassifierSettings settings = ClassifierSettingsFile.Load(Write("{ \"likelySpamThreshold\": 40 }"));

        Assert.Equal(40, settings.LikelySpamThreshold);
        Assert.Equal(70, settings.CertainSpamThreshold);
        AssertSameTlds(ClassifierSettings.Default.SuspiciousTlds, settings);
        AssertSameFreemail(ClassifierSettings.Default.FreemailDomains, settings);
    }

    [Fact]
    public void Certain_threshold_key_overrides_only_that_value()
    {
        ClassifierSettings settings = ClassifierSettingsFile.Load(Write("{ \"certainSpamThreshold\": 90 }"));

        Assert.Equal(30, settings.LikelySpamThreshold);
        Assert.Equal(90, settings.CertainSpamThreshold);
    }

    // Case 15
    [Fact]
    public void Given_tld_list_replaces_the_default_list_completely()
    {
        ClassifierSettings settings = ClassifierSettingsFile.Load(Write("{ \"suspiciousTlds\": [\"zip\"] }"));

        Assert.Equal(new[] { "zip" }, settings.SuspiciousTlds.ToArray());
        Assert.Equal(30, settings.LikelySpamThreshold);
        AssertSameFreemail(ClassifierSettings.Default.FreemailDomains, settings);
    }

    [Fact]
    public void Given_freemail_list_replaces_the_default_list_completely()
    {
        ClassifierSettings settings = ClassifierSettingsFile.Load(
            Write("{ \"freemailDomains\": [\"mail.example.test\", \"post.example.test\"] }"));

        AssertSameFreemail(["mail.example.test", "post.example.test"], settings);
        AssertSameTlds(ClassifierSettings.Default.SuspiciousTlds, settings);
    }

    [Fact]
    public void All_keys_can_be_given_together()
    {
        const string json = """
            {
              "likelySpamThreshold": 25,
              "certainSpamThreshold": 60,
              "suspiciousTlds": ["zip", "mov"],
              "freemailDomains": ["mail.example.test"]
            }
            """;

        ClassifierSettings settings = ClassifierSettingsFile.Load(Write(json));

        Assert.Equal(25, settings.LikelySpamThreshold);
        Assert.Equal(60, settings.CertainSpamThreshold);
        AssertSameTlds(["zip", "mov"], settings);
        AssertSameFreemail(["mail.example.test"], settings);
    }

    // Case 20
    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        const string json = """
            {
              // raise the first threshold
              "likelySpamThreshold": 40, /* inline */
              "suspiciousTlds": ["zip", "mov",],
            }
            """;

        ClassifierSettings settings = ClassifierSettingsFile.Load(Write(json));

        Assert.Equal(40, settings.LikelySpamThreshold);
        AssertSameTlds(["zip", "mov"], settings);
    }

    [Fact]
    public void Loaded_settings_are_valid()
    {
        ClassifierSettings settings = ClassifierSettingsFile.Load(Write("{ \"likelySpamThreshold\": 40 }"));

        settings.Validate();
    }

    // ---- error cases ---------------------------------------------------------------------------------------

    // Case 16
    [Fact]
    public void Unknown_key_is_rejected_and_named()
    {
        AssertLoadFails("{ \"likelySpamTreshold\": 40 }", "likelySpamTreshold");
    }

    [Fact]
    public void Keys_are_case_sensitive()
    {
        AssertLoadFails("{ \"LikelySpamThreshold\": 40 }", "LikelySpamThreshold");
    }

    // Case 17
    [Fact]
    public void String_instead_of_number_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"certainSpamThreshold\": \"70\" }", "certainSpamThreshold");
    }

    [Fact]
    public void Fractional_number_for_a_threshold_is_rejected()
    {
        AssertLoadFails("{ \"likelySpamThreshold\": 30.5 }", "likelySpamThreshold");
    }

    [Fact]
    public void String_instead_of_list_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"suspiciousTlds\": \"top\" }", "suspiciousTlds");
    }

    [Fact]
    public void Number_as_list_entry_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"freemailDomains\": [\"mail.example.test\", 5] }", "freemailDomains");
    }

    [Fact]
    public void Null_list_entry_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"suspiciousTlds\": [\"zip\", null] }", "suspiciousTlds");
    }

    // Case 18
    [Fact]
    public void Inverted_thresholds_fail_validation_with_inner_argument_exception()
    {
        InvalidDataException exception = AssertLoadFails("{ \"likelySpamThreshold\": 70, \"certainSpamThreshold\": 30 }");

        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    [Theory]
    [InlineData("{ \"likelySpamThreshold\": 80 }")] // above the default certain threshold 70
    [InlineData("{ \"certainSpamThreshold\": 20 }")] // below the default likely threshold 30
    [InlineData("{ \"likelySpamThreshold\": 0 }")]
    [InlineData("{ \"suspiciousTlds\": [\"\"] }")]
    [InlineData("{ \"freemailDomains\": [\"\"] }")]
    public void Values_that_fail_validation_are_rejected_with_inner_argument_exception(string json)
    {
        InvalidDataException exception = AssertLoadFails(json);

        Assert.IsAssignableFrom<ArgumentException>(exception.InnerException);
    }

    // Case 19
    [Theory]
    [InlineData("{ \"likelySpamThreshold\": ")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_json_is_rejected_with_file_name_only(string content)
    {
        AssertLoadFails(content);
    }

    // Case 19
    [Theory]
    [InlineData("[]")]
    [InlineData("[40]")]
    [InlineData("40")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Root_that_is_not_an_object_is_rejected_with_file_name_only(string content)
    {
        AssertLoadFails(content);
    }

    [Fact]
    public void Error_message_names_the_actual_file_name_of_the_given_path()
    {
        AssertLoadFails("[]", fileName: "custom-settings.json");
    }
}

using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07c D3 / case 3: loading <see cref="ClassificationPolicy"/> from <c>classification-policy.json</c>.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPolicyFileTests : IDisposable
{
    // Pure, synchronous file IO: no per-test timeout needed (xUnit v2 cannot interrupt sync code).
    private readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private string Write(string content, string fileName = ClassificationPolicyFile.FileName)
    {
        string path = Path.Combine(directory.RootPath, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Contract for load errors: InvalidDataException, file name present, directory and rejected value absent.</summary>
    private InvalidDataException AssertLoadFails(
        string content, string? expectedKey = null, string? valueThatMustNotAppear = null, string fileName = ClassificationPolicyFile.FileName)
    {
        string path = Write(content, fileName);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ClassificationPolicyFile.Load(path));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFileName(directory.RootPath), exception.Message, StringComparison.OrdinalIgnoreCase);
        if (expectedKey is not null)
        {
            Assert.Contains(expectedKey, exception.Message, StringComparison.Ordinal);
        }

        if (valueThatMustNotAppear is not null)
        {
            Assert.DoesNotContain(valueThatMustNotAppear, exception.Message, StringComparison.Ordinal);
        }

        return exception;
    }

    [Fact]
    public void FileName_is_classification_policy_json()
    {
        Assert.Equal("classification-policy.json", ClassificationPolicyFile.FileName);
    }

    // Case 3
    [Fact]
    [Trait("Category", "smoke")]
    public void Missing_file_yields_the_default_policy()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(Path.Combine(directory.RootPath, ClassificationPolicyFile.FileName));

        Assert.Equal(ClassificationPolicy.Default, policy);
        Assert.Equal(ClassificationMode.RecordOnly, policy.Mode);
        Assert.Equal(0, policy.QuarantineRetentionDays);
    }

    [Fact]
    public void Empty_object_keeps_all_defaults()
    {
        Assert.Equal(ClassificationPolicy.Default, ClassificationPolicyFile.Load(Write("{}")));
    }

    // Case 3: partial files
    [Fact]
    public void Mode_alone_keeps_the_default_retention()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(Write("{ \"mode\": \"act\" }"));

        Assert.Equal(ClassificationMode.Act, policy.Mode);
        Assert.Equal(0, policy.QuarantineRetentionDays);
    }

    [Fact]
    public void Retention_alone_keeps_the_default_mode()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(Write("{ \"quarantineRetentionDays\": 30 }"));

        Assert.Equal(ClassificationMode.RecordOnly, policy.Mode);
        Assert.Equal(30, policy.QuarantineRetentionDays);
    }

    [Fact]
    public void Both_keys_can_be_given()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(
            Write("{ \"mode\": \"recordOnly\", \"quarantineRetentionDays\": 3650 }"));

        Assert.Equal(new ClassificationPolicy(ClassificationMode.RecordOnly, 3650), policy);
    }

    [Theory]
    [InlineData("recordOnly", ClassificationMode.RecordOnly)]
    [InlineData("act", ClassificationMode.Act)]
    public void Mode_strings_map_to_the_modes(string text, ClassificationMode expected)
    {
        Assert.Equal(expected, ClassificationPolicyFile.Load(Write($"{{ \"mode\": \"{text}\" }}")).Mode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3650)]
    public void Retention_boundaries_are_accepted(int days)
    {
        Assert.Equal(days, ClassificationPolicyFile.Load(Write($"{{ \"quarantineRetentionDays\": {days} }}")).QuarantineRetentionDays);
    }

    [Fact]
    public void Loaded_policy_is_valid()
    {
        ClassificationPolicyFile.Load(Write("{ \"mode\": \"act\", \"quarantineRetentionDays\": 14 }")).Validate();
    }

    // ---- error cases (case 3) -----------------------------------------------------------------------------------

    [Fact]
    public void Unknown_key_is_rejected_and_named()
    {
        AssertLoadFails("{ \"modee\": \"act\" }", "modee");
    }

    [Fact]
    public void Keys_are_case_sensitive()
    {
        AssertLoadFails("{ \"Mode\": \"act\" }", "Mode");
    }

    [Theory]
    [InlineData("{ \"mode\": \"act\", \"mode\": \"recordOnly\" }", "mode")]
    [InlineData("{ \"quarantineRetentionDays\": 5, \"quarantineRetentionDays\": 6 }", "quarantineRetentionDays")]
    public void Duplicate_key_is_rejected_and_named(string json, string key)
    {
        AssertLoadFails(json, key);
    }

    [Fact]
    public void Number_instead_of_mode_string_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"mode\": 2 }", "mode");
    }

    [Fact]
    public void Null_mode_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"mode\": null }", "mode");
    }

    [Fact]
    public void Unknown_mode_text_is_rejected_names_the_key_but_not_the_value()
    {
        AssertLoadFails("{ \"mode\": \"zzunknownmodevalue\" }", "mode", "zzunknownmodevalue");
    }

    [Fact]
    public void String_instead_of_number_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"quarantineRetentionDays\": \"30\" }", "quarantineRetentionDays");
    }

    [Fact]
    public void Fractional_number_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"quarantineRetentionDays\": 30.5 }", "quarantineRetentionDays");
    }

    [Fact]
    public void Null_retention_is_rejected_and_names_the_key()
    {
        AssertLoadFails("{ \"quarantineRetentionDays\": null }", "quarantineRetentionDays");
    }

    [Theory]
    [InlineData("-1", "-1")]
    [InlineData("3651", "3651")]
    [InlineData("987654321", "987654321")]
    public void Retention_outside_the_range_is_rejected_names_the_key_but_not_the_value(string days, string value)
    {
        AssertLoadFails($"{{ \"quarantineRetentionDays\": {days} }}", "quarantineRetentionDays", value);
    }

    [Theory]
    [InlineData("{ \"mode\": ")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_json_is_rejected_with_file_name_only(string content)
    {
        AssertLoadFails(content);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("30")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void Root_that_is_not_an_object_is_rejected_with_file_name_only(string content)
    {
        AssertLoadFails(content);
    }

    [Fact]
    public void Error_message_names_the_actual_file_name_of_the_given_path()
    {
        AssertLoadFails("[]", fileName: "custom-policy.json");
    }
}

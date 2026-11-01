using MailWarden.Core.Classification;
using MailWarden.Infrastructure.Classification;

namespace MailWarden.Infrastructure.Tests.Classification;

/// <summary>AP-07d D2 / case 7: the optional key <c>maxMovesPerPass</c> of <c>classification-policy.json</c>.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPolicyMaxMovesFileTests : IDisposable
{
    private const string Key = "maxMovesPerPass";

    // Pure, synchronous file IO: no per-test timeout needed (xUnit v2 cannot interrupt sync code).
    private readonly TempDirectory directory = new();

    public void Dispose() => directory.Dispose();

    private string Write(string content)
    {
        string path = Path.Combine(directory.RootPath, ClassificationPolicyFile.FileName);
        File.WriteAllText(path, content);
        return path;
    }

    private void AssertLoadFails(string content, string? valueThatMustNotAppear = null)
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ClassificationPolicyFile.Load(Write(content)));

        Assert.Contains(ClassificationPolicyFile.FileName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(Key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(directory.RootPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        if (valueThatMustNotAppear is not null)
        {
            Assert.DoesNotContain(valueThatMustNotAppear, exception.Message, StringComparison.Ordinal);
        }
    }

    // Case 7: missing file and missing key give the safe default of 100.
    [Fact]
    [Trait("Category", "smoke")]
    public void Missing_file_has_the_default_limit_of_100()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(Path.Combine(directory.RootPath, ClassificationPolicyFile.FileName));

        Assert.Equal(100, policy.MaxMovesPerPass);
        Assert.Equal(ClassificationPolicy.Default, policy);
    }

    [Fact]
    public void Missing_key_keeps_the_default_limit_and_the_other_keys()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(Write("{ \"mode\": \"act\", \"quarantineRetentionDays\": 14 }"));

        Assert.Equal(100, policy.MaxMovesPerPass);
        Assert.Equal(ClassificationMode.Act, policy.Mode);
        Assert.Equal(14, policy.QuarantineRetentionDays);
    }

    // Case 7: valid values including both range boundaries.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(1000)]
    public void Valid_limits_are_loaded(int limit)
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(Write($"{{ \"{Key}\": {limit} }}"));

        Assert.Equal(limit, policy.MaxMovesPerPass);
        Assert.Equal(ClassificationMode.RecordOnly, policy.Mode);
        policy.Validate();
    }

    [Fact]
    public void Limit_can_be_combined_with_the_other_keys()
    {
        ClassificationPolicy policy = ClassificationPolicyFile.Load(
            Write($"{{ \"mode\": \"act\", \"quarantineRetentionDays\": 30, \"{Key}\": 25 }}"));

        Assert.Equal(new ClassificationPolicy(ClassificationMode.Act, 30, 25), policy);
    }

    // Case 7: outside the range, names the key but not the value.
    [Theory]
    [InlineData("0", null)]
    [InlineData("-1", "-1")]
    [InlineData("1001", "1001")]
    [InlineData("987654321", "987654321")]
    public void Limit_outside_the_range_is_rejected(string limit, string? value)
    {
        AssertLoadFails($"{{ \"{Key}\": {limit} }}", value);
    }

    // Case 7: wrong type.
    [Theory]
    [InlineData("\"5\"")]
    [InlineData("5.5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Limit_of_the_wrong_type_is_rejected(string literal)
    {
        AssertLoadFails($"{{ \"{Key}\": {literal} }}");
    }

    // Existing strictness: duplicates and wrong case are rejected as for the other keys.
    [Fact]
    public void Duplicate_limit_key_is_rejected()
    {
        AssertLoadFails($"{{ \"{Key}\": 5, \"{Key}\": 6 }}");
    }

    [Fact]
    public void Limit_key_is_case_sensitive()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ClassificationPolicyFile.Load(Write("{ \"MaxMovesPerPass\": 5 }")));

        Assert.Contains("MaxMovesPerPass", exception.Message, StringComparison.Ordinal);
    }
}

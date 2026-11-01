namespace MailWarden.Core.Tests.Classification;

/// <summary>AP-07c D1 / case 3: <see cref="ClassificationPolicy"/> default and validation.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPolicyTests
{
    [Fact]
    [Trait("Category", "smoke")]
    public void Default_is_record_only_without_retention()
    {
        ClassificationPolicy policy = ClassificationPolicy.Default;

        Assert.Equal(ClassificationMode.RecordOnly, policy.Mode);
        Assert.Equal(0, policy.QuarantineRetentionDays);
    }

    [Fact]
    public void Default_policy_is_valid()
    {
        ClassificationPolicy.Default.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(3650)]
    public void Retention_days_inside_the_range_are_valid(int days)
    {
        new ClassificationPolicy(ClassificationMode.Act, days).Validate();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Retention_days_outside_the_range_are_rejected(int days)
    {
        var policy = new ClassificationPolicy(ClassificationMode.RecordOnly, days);

        Assert.ThrowsAny<ArgumentException>(policy.Validate);
    }

    [Fact]
    public void Mode_values_are_the_documented_numbers()
    {
        Assert.Equal(1, (int)ClassificationMode.RecordOnly);
        Assert.Equal(2, (int)ClassificationMode.Act);
    }
}

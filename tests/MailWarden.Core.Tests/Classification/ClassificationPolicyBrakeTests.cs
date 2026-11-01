namespace MailWarden.Core.Tests.Classification;

/// <summary>AP-07d D2 / case 7: <see cref="ClassificationPolicy.MaxMovesPerPass"/> and the outcome <see cref="ClassificationOutcome.BrakeHeld"/>.</summary>
[Trait("Category", "feature:classifier")]
public sealed class ClassificationPolicyBrakeTests
{
    [Fact]
    [Trait("Category", "smoke")]
    public void Default_limit_is_100_and_the_default_policy_stays_valid()
    {
        Assert.Equal(100, ClassificationPolicy.Default.MaxMovesPerPass);
        Assert.Equal(100, new ClassificationPolicy(ClassificationMode.Act, 0).MaxMovesPerPass);
        ClassificationPolicy.Default.Validate();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Limit_inside_the_range_is_valid(int limit)
    {
        new ClassificationPolicy(ClassificationMode.Act, 0, limit).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Limit_outside_the_range_is_rejected(int limit)
    {
        var policy = new ClassificationPolicy(ClassificationMode.RecordOnly, 0, limit);

        Assert.ThrowsAny<ArgumentException>(policy.Validate);
    }

    [Fact]
    public void Limit_takes_part_in_equality()
    {
        Assert.NotEqual(ClassificationPolicy.Default, ClassificationPolicy.Default with { MaxMovesPerPass = 7 });
    }

    [Fact]
    public void Brake_held_outcome_is_the_documented_number_and_does_not_reuse_another_value()
    {
        Assert.Equal(8, (int)ClassificationOutcome.BrakeHeld);
        Assert.Equal(7, (int)ClassificationOutcome.EvaluationFailed);
    }
}

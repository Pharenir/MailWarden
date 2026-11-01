namespace MailWarden.Core.Classification;

/// <summary>
/// Safety settings of the classification pass.
/// </summary>
/// <param name="Mode">Whether decisions are only recorded or also acted on.</param>
/// <param name="QuarantineRetentionDays">Days after the stored decision time at which mail this app moved to the
/// Quarantine is deleted; 0 = never delete.</param>
public sealed record ClassificationPolicy(ClassificationMode Mode, int QuarantineRetentionDays)
{
    /// <summary>Largest allowed <see cref="QuarantineRetentionDays"/>.</summary>
    public const int MaxQuarantineRetentionDays = 3650;

    /// <summary>The safe default: <see cref="ClassificationMode.RecordOnly"/>, retention 0 (never delete).</summary>
    public static ClassificationPolicy Default { get; } = new(ClassificationMode.RecordOnly, 0);

    /// <summary>Checks the policy (fail fast when loading).</summary>
    /// <exception cref="ArgumentException"><see cref="QuarantineRetentionDays"/> is below 0 or above
    /// <see cref="MaxQuarantineRetentionDays"/>, or <see cref="Mode"/> is not a defined value.</exception>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentException("The classification mode is not a defined value.");
        }

        if (QuarantineRetentionDays < 0 || QuarantineRetentionDays > MaxQuarantineRetentionDays)
        {
            throw new ArgumentException(
                $"The quarantine retention must be between 0 and {MaxQuarantineRetentionDays} days.");
        }
    }
}


namespace RecycleBinToolkit;

/// <summary>
/// Decides the answer to "is the Recycle Bin in use for this volume" from the facts Windows exposes.
/// </summary>
/// <remarks>
/// Split from the reading on purpose. Gathering the facts means touching the registry and the volume
/// table, which cannot be arranged from a test without changing the machine's own settings; the decision
/// itself can be checked exhaustively, and it is the decision that decides what a caller is told.
/// </remarks>
internal static class RecycleBinVolumePolicy
{
    /// <summary>
    /// Chooses the reason from the gathered facts.
    /// </summary>
    /// <param name="volumeExists">The volume is mounted and the path given is its root.</param>
    /// <param name="driveType">The volume's type.</param>
    /// <param name="disabledByPolicy">A group policy forbids the Recycle Bin.</param>
    /// <param name="nukeOnDelete">The volume's own setting deletes immediately.</param>
    /// <returns>The reason to report.</returns>
    /// <remarks>
    /// The order is the order in which the reasons override each other. A policy is named first because
    /// it is the only one the caller cannot fix, which makes it the most useful thing to say when it
    /// applies. An unreadable volume type outranks the unsupported-type answer, because a type that
    /// could not be read says nothing about whether the volume supports a Recycle Bin — it only says
    /// this call does not know.
    /// </remarks>
    internal static RecycleBinAvailabilityReason Classify(
        bool volumeExists,
        DriveType driveType,
        bool disabledByPolicy,
        bool nukeOnDelete)
    {
        if (!volumeExists)
        {
            return RecycleBinAvailabilityReason.VolumeNotFound;
        }

        if (disabledByPolicy)
        {
            return RecycleBinAvailabilityReason.DisabledByPolicy;
        }

        // Enabled is the one answer that asserts a fact, and this library does not assert what it could
        // not read.
        if (driveType == DriveType.Unknown)
        {
            return RecycleBinAvailabilityReason.Indeterminate;
        }

        if (driveType is not (DriveType.Fixed or DriveType.Removable))
        {
            return RecycleBinAvailabilityReason.UnsupportedVolumeType;
        }

        if (nukeOnDelete)
        {
            return RecycleBinAvailabilityReason.DisabledByUserPreference;
        }

        return RecycleBinAvailabilityReason.Enabled;
    }

    /// <summary>
    /// Decides whether a reason stops the path from being handed to the Shell at all.
    /// </summary>
    /// <param name="reason">The reason <see cref="Classify"/> — or a probe — produced.</param>
    /// <param name="action">What the caller asked for on a volume that cannot recycle.</param>
    /// <returns><c>true</c> when the path is refused, with the file left where it is.</returns>
    /// <remarks>
    /// <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/> sits with
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/> rather than with the confirmed
    /// refusals. A failed probe is strong evidence — an empty bin still queries successfully — but it
    /// cannot exclude lazy bin creation on a machine other than this one, and treating it as final would
    /// leave such a machine with no way to recycle at all except
    /// <see cref="NoRecycleBinAction.DeletePermanently"/>, that is, no way except to destroy the file.
    /// <para>
    /// <see cref="RecycleBinAvailabilityReason.VolumeNotFound"/> ignores the setting: the volume holding
    /// the path is not mounted, so there is nothing to hand over and nothing to destroy. A path that is
    /// itself missing is a different case, and it is rejected earlier, at
    /// <see cref="RecycleFailureStage.PathValidation"/>.
    /// </para>
    /// </remarks>
    internal static bool RefusesRecycle(RecycleBinAvailabilityReason reason, NoRecycleBinAction action) =>
        reason switch
        {
            RecycleBinAvailabilityReason.Enabled => false,
            RecycleBinAvailabilityReason.DisabledByPolicy
                or RecycleBinAvailabilityReason.DisabledByUserPreference
                or RecycleBinAvailabilityReason.UnsupportedVolumeType => action != NoRecycleBinAction.DeletePermanently,
            RecycleBinAvailabilityReason.Indeterminate
                or RecycleBinAvailabilityReason.NoRecycleBinOnVolume => action == NoRecycleBinAction.Throw,
            RecycleBinAvailabilityReason.VolumeNotFound => true,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "An unnamed reason has no decision to make, so a new one has to be given one here.")
        };
}


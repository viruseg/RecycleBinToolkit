
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
}


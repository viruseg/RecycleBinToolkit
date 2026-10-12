namespace RecycleBinToolkit;

/// <summary>Why the Recycle Bin is or is not in use for a volume.</summary>
/// <remarks>
/// Which answer applies decides what a caller can do about it, which is why this is not a bare
/// "off". Values are numbered explicitly and the numbers are part of the contract: a new member names its
/// number above the highest declared here.
/// </remarks>
public enum RecycleBinAvailabilityReason
{
    /// <summary>Nothing disables the Recycle Bin for this volume.</summary>
    Enabled = 0,

    /// <summary>A group policy forbids it. Neither the user nor the application can change this.</summary>
    DisabledByPolicy = 1,

    /// <summary>
    /// This volume's own setting deletes files immediately instead of recycling them. The user can change it.
    /// </summary>
    DisabledByUserPreference = 2,

    /// <summary>The volume type has no Recycle Bin.</summary>
    UnsupportedVolumeType = 3,

    /// <summary>The volume is not mounted, or the path given is not a volume root.</summary>
    VolumeNotFound = 4,

    /// <summary>
    /// The volume supports a Recycle Bin, but none was ever recorded for it. No longer produced: an
    /// unconfigured volume answers <see cref="Indeterminate"/>. Kept for contract stability.
    /// </summary>
    NoRecycleBinOnVolume = 5,

    /// <summary>
    /// The volume exists, but configuration could not confirm it — either its type could not be read, or
    /// nothing was recorded for it. Deliberately not <see cref="Enabled"/>.
    /// </summary>
    Indeterminate = 6,
}

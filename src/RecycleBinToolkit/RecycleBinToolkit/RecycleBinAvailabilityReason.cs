namespace RecycleBinToolkit;

/// <summary>
/// Why the Recycle Bin is or is not in use for a volume.
/// </summary>
/// <remarks>
/// Whether a volume recycles is decided by a group policy, by a per-volume setting the user owns, and by the
/// kind of volume, so an answer that is only "off" would throw away the one thing a caller can act on.
/// <para>
/// Every member is numbered explicitly and the numbers are part of the contract: an implicit value renumbers
/// every member declared after an insertion, which compiles everywhere and silently breaks every caller that
/// persisted one. A new member therefore names its number above the highest declared here.
/// </para>
/// </remarks>
public enum RecycleBinAvailabilityReason
{
    /// <summary>Nothing disables the Recycle Bin for this volume.</summary>
    Enabled = 0,

    /// <summary>
    /// A group policy forbids the Recycle Bin. Neither the user nor the application can change this, which is
    /// why it is named first whenever it applies.
    /// </summary>
    DisabledByPolicy = 1,

    /// <summary>
    /// This volume's own setting deletes files immediately instead of recycling them — the "Don't move files
    /// to the Recycle Bin" choice in the Recycle Bin properties dialog. The user can change it.
    /// </summary>
    DisabledByUserPreference = 2,

    /// <summary>The volume type has no Recycle Bin. Network drives never had one; a CD-ROM or a floppy cannot.</summary>
    UnsupportedVolumeType = 3,

    /// <summary>The volume is not mounted, or the path given is not a volume root.</summary>
    VolumeNotFound = 4,

    /// <summary>
    /// The volume exists and its type supports a Recycle Bin, but no bin was ever recorded for it. Nothing in
    /// this library produces this reason anymore — an unconfigured volume answers <see cref="Indeterminate"/>
    /// — and the member stays because the numbers are contract.
    /// </summary>
    NoRecycleBinOnVolume = 5,

    /// <summary>
    /// The volume exists, but configuration could not confirm it: either its type could not be read, or
    /// nothing was recorded for it. Deliberately not reported as <see cref="Enabled"/> — a volume Windows has
    /// never configured has no setting of its own, and saying "enabled" would be a guess.
    /// </summary>
    Indeterminate = 6,
}

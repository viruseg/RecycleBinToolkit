namespace RecycleBinToolkit;

/// <summary>
/// Why the Recycle Bin is or is not in use for a volume.
/// </summary>
/// <remarks>
/// Whether a volume recycles is decided by a group policy, by a per-volume setting the user owns, and by
/// the kind of volume: an answer that is only "off" throws away the one thing the caller needs to do
/// something about it.
/// <para>
/// The members are numbered explicitly because this is a public enum and the numbers are part of its
/// contract: an implicit value renumbers every member declared after an insertion, which compiles
/// everywhere and silently breaks every caller that persisted one. Adding a member therefore means
/// choosing its number explicitly, above the last one declared here — never relying on the next value,
/// which is the count of members rather than one past the highest.
/// </para>
/// </remarks>
public enum RecycleBinAvailabilityReason
{
    /// <summary>Nothing disables the Recycle Bin for this volume.</summary>
    Enabled = 0,

    /// <summary>
    /// A group policy forbids the Recycle Bin. The user cannot change this, and neither can the
    /// application.
    /// </summary>
    DisabledByPolicy = 1,

    /// <summary>
    /// This volume's own setting deletes files immediately instead of recycling them, which is the
    /// "Don't move files to the Recycle Bin" choice in the Recycle Bin properties dialog. The user can
    /// change it.
    /// </summary>
    DisabledByUserPreference = 2,

    /// <summary>
    /// The volume type has no Recycle Bin. Network drives never had one; a CD-ROM or a floppy cannot.
    /// </summary>
    UnsupportedVolumeType = 3,

    /// <summary>The volume is not mounted, or the path given is not a volume root.</summary>
    VolumeNotFound = 4,

    /// <summary>
    /// The volume exists and its type supports a Recycle Bin, but no bin was ever recorded for it.
    /// Nothing in this library produces this reason anymore: without a probe it cannot be told apart
    /// from <see cref="Indeterminate"/>, which is what an unconfigured volume answers. The member stays
    /// because the numbers are contract.
    /// </summary>
    NoRecycleBinOnVolume = 5,

    /// <summary>
    /// The volume exists, but configuration could not confirm it: either its type could not be read, or
    /// nothing was recorded for it.
    /// </summary>
    /// <remarks>
    /// Deliberately not reported as <see cref="Enabled"/>. The second cause is the common one: a volume
    /// Windows has never configured has no key of its own.
    /// </remarks>
    Indeterminate = 6,
}

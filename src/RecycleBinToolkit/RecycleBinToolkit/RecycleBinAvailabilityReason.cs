namespace RecycleBinToolkit;

/// <summary>
/// Why the Recycle Bin is or is not in use for a volume.
/// </summary>
/// <remarks>
/// <c>SHQueryRecycleBin</c> is the one query that answers whether a volume recycles at all, and this
/// library asks it. It cannot say which of the reasons below applies, because what happens on the
/// next delete is decided by a group policy, by a per-volume setting the user owns, and by the kind
/// of volume: an answer that is only "off" throws away the one thing the caller needs to do something
/// about it.
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
    /// The volume exists and its type supports a Recycle Bin, but the Shell reports that it has none.
    /// </summary>
    /// <remarks>
    /// Measured on the machine this library was developed against: <c>SHQueryRecycleBin</c> returned
    /// <c>0x80004005</c> on a removable volume with no <c>$RECYCLE.BIN</c>, which had deleted every file
    /// handed to it, permanently, while reporting success.
    /// <para>
    /// The same query against a share with no bin answered <c>0x80070003</c>, and that is the evidence
    /// this probe rests on: a volume with no bin gets a refusal of its own rather than a success, so a
    /// failed query says something. A share is settled as <see cref="UnsupportedVolumeType"/> from its
    /// own path before the probe is reached, so that code is never carried by this reason.
    /// </para>
    /// </remarks>
    NoRecycleBinOnVolume = 5,

    /// <summary>
    /// The volume exists, but configuration could not confirm it: either its type could not be read, or
    /// nothing was recorded for it and the Shell was asked and reported a bin.
    /// </summary>
    /// <remarks>
    /// Deliberately not reported as <see cref="Enabled"/>. The second cause is the common one now that
    /// the Shell is consulted: a volume Windows has never configured has no key of its own, and a probe
    /// that found a bin says nothing that would tell it apart from a volume that was configured.
    /// </remarks>
    Indeterminate = 6,
}

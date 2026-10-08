namespace RecycleBinToolkit;

/// <summary>
/// Why the Recycle Bin is or is not in use for a volume.
/// </summary>
/// <remarks>
/// Windows exposes no single authoritative query. What happens on the next delete is decided by a
/// group policy, by a per-volume setting the user owns, and by the kind of volume, so an answer that
/// is only "off" throws away the one thing the caller needs to do something about it.
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
    /// <c>0x80004005</c> on a removable volume with no <c>$RECYCLE.BIN</c>, and <c>0x80070003</c> on a
    /// network share. Both had deleted every file handed to them, permanently, while reporting success.
    /// </remarks>
    NoRecycleBinOnVolume = 5,

    /// <summary>
    /// The volume exists but the answer could not be established — its volume GUID, which is what the
    /// per-volume setting is keyed on, could not be read. Deliberately not reported as enabled.
    /// </summary>
    Indeterminate = 6,
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the facts about one volume and turns them into a <see cref="RecycleBinStatus"/>.
/// </summary>
/// <remarks>
/// Reads what the volume table and the registry say, and asks the Shell only about the volumes those
/// two say nothing about. It runs outside the Shell worker, so it neither needs that worker nor waits
/// behind an operation already queued on it.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RecycleBinVolumeState
{
    private const int GuidPathCapacity = 64;

    /// <summary>
    /// Inspects a volume.
    /// </summary>
    /// <param name="volumeRoot">Volume root, with or without a trailing separator.</param>
    /// <returns>The status of that volume.</returns>
    /// <remarks>
    /// The Shell is consulted for a volume that the registry and the volume table say nothing about, and
    /// <see cref="RecycleBinCounter.TryQuery"/> is the only question asked: an empty Recycle Bin still
    /// answers it successfully, so a failure names a volume that has no bin at all rather than a volume
    /// whose bin happens to be full of nothing.
    /// <para>
    /// The probe runs last because it is the expensive step. <c>SHQueryRecycleBin</c> walks the bin's
    /// metadata, which took about 11 seconds against a bin of 90,000 entries, so a volume whose own key
    /// reads <c>NukeOnDelete=0</c> is answered from configuration and never reaches it.
    /// </para>
    /// </remarks>
    internal static RecycleBinStatus Inspect(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        var root = NormalizeVolumeRoot(volumeRoot);

        // A path that is not a volume root is not a question about a volume, and a drive letter that
        // is not mounted has no volume table entry behind it. Both are answered before anything else
        // so a caller never reads them as a Recycle Bin that happens to be off.
        if (string.IsNullOrEmpty(Path.GetPathRoot(volumeRoot)) || !IsVolumeRoot(volumeRoot) || !Directory.Exists(root))
        {
            return new RecycleBinStatus(root, DriveType.Unknown, RecycleBinAvailabilityReason.VolumeNotFound);
        }

        var driveType = ReadDriveType(root);
        var disabledByPolicy = RecycleBinRegistry.IsDisabledByPolicy();

        // The per-volume setting lives under a subkey named after the volume's GUID, so a volume whose
        // GUID cannot be read has no setting to read, and its configuration says nothing either way.
        var volumeKeyName = TryGetVolumeGuidKeyName(root);
        var nukeOnDelete = volumeKeyName is not null && RecycleBinRegistry.IsNukeOnDelete(volumeKeyName);
        var configured = RecycleBinVolumePolicy.Classify(volumeExists: true, driveType, disabledByPolicy, nukeOnDelete);

        // A policy, a volume type that never had a bin, and a volume set to delete outright are all
        // reasons the caller can act on, and each of them is settled above. The Shell could only
        // contradict one, which would throw away the reason the caller needs.
        if (configured is not (RecycleBinAvailabilityReason.Enabled or RecycleBinAvailabilityReason.Indeterminate))
        {
            return new RecycleBinStatus(root, driveType, configured);
        }

        // A volume whose own key answered is finished, and answering from that key is what keeps the
        // probe off the hot path: a volume reading NukeOnDelete=0 never reaches the Shell at all.
        if (volumeKeyName is not null)
        {
            return new RecycleBinStatus(root, driveType, configured);
        }

        // What configuration is silent about is settled by asking whether the bin is there. A volume that
        // has one says nothing to tell it from a configured one, which is what Indeterminate means; a
        // volume that has none is named, because a delete handed to the Shell on such a volume is
        // removed for good.
        if (!RecycleBinCounter.TryQuery(root, out _, out _))
        {
            return new RecycleBinStatus(root, driveType, RecycleBinAvailabilityReason.NoRecycleBinOnVolume);
        }

        return new RecycleBinStatus(root, driveType, RecycleBinAvailabilityReason.Indeterminate);
    }

    /// <summary>
    /// Whether the path is a volume root rather than something inside one. The trailing separator is
    /// what separates them, and it is what callers get wrong.
    /// </summary>
    private static bool IsVolumeRoot(string volumeRoot) =>
        string.Equals(volumeRoot, Path.GetPathRoot(volumeRoot), StringComparison.OrdinalIgnoreCase);

    private static DriveType ReadDriveType(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DriveType.Unknown;
        }
    }

    /// <summary>
    /// Reads the name the per-volume Recycle Bin setting is keyed on: the volume's GUID in braces,
    /// which is what the subkey under the Shell's own per-volume list is called.
    /// </summary>
    /// <remarks>
    /// Not the <c>\\?\Volume{…}\</c> device path the volume API answers with. That path is how Win32
    /// names the volume; the registry calls the same volume by the bare GUID in braces, and building a
    /// subkey name out of the device path misses every time — which reports a volume whose recycling is
    /// switched off as enabled.
    /// </remarks>
    /// <returns>The braced GUID, or <see langword="null"/> when it could not be read.</returns>
    internal static string? TryGetVolumeGuidKeyName(string root)
    {
        var buffer = new char[GuidPathCapacity];

        if (!PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer))
        {
            return null;
        }

        // The volume API null-terminates its answer and leaves the rest of the buffer alone, so the name
        // ends at the first terminator rather than at the end of the array. Reading to the end instead
        // puts fifteen NUL characters into every subkey name built from it.
        var written = Array.IndexOf(buffer, '\0');
        if (written < 0)
        {
            return null;
        }

        var path = buffer.AsSpan(0, written);
        var open = path.IndexOf('{');
        var close = path.IndexOf('}');

        if (open < 0 || close < open)
        {
            return null;
        }

        return path[open..(close + 1)].ToString();
    }

    /// <summary>
    /// The volume a path belongs to, always in the form the Shell and the volume APIs accept: a root with
    /// a trailing separator and no extended-length prefix.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetPathRoot(string)"/> answers <c>E:</c> for a bare drive letter, which is a
    /// drive-relative path rather than a root and is rejected by
    /// <c>GetVolumeNameForVolumeMountPoint</c>. Collapsing to a root alone is therefore not enough: the
    /// separator has to be added back.
    /// <para>
    /// The extended-length prefix comes off for the same reason it comes off everywhere else here:
    /// <see cref="DriveInfo"/> throws on <c>\\?\E:\</c>, so a prefixed root is handed back as a volume
    /// whose type could not be read and is reported Indeterminate rather than as the fact it is.
    /// Reducing every spelling of a root to one form is what makes the answer independent of spelling.
    /// </para>
    /// </remarks>
    /// <param name="volumeRoot">A volume root or any path inside one.</param>
    internal static string NormalizeVolumeRoot(string volumeRoot)
    {
        var root = Path.GetPathRoot(volumeRoot);
        if (string.IsNullOrEmpty(root))
        {
            return AppendSeparator(RecycleTarget.StripExtendedPrefix(volumeRoot));
        }

        var shellForm = RecycleTarget.StripExtendedPrefix(root);

        return shellForm.EndsWith(Path.DirectorySeparatorChar) || shellForm.EndsWith(Path.AltDirectorySeparatorChar)
            ? shellForm
            : shellForm + Path.DirectorySeparatorChar;
    }

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

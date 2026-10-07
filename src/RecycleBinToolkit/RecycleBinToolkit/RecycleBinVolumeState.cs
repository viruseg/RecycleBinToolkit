using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the facts about one volume and turns them into a <see cref="RecycleBinStatus"/>.
/// </summary>
/// <remarks>
/// Reads only what the volume table and the registry say. It makes no COM call, so it neither needs
/// nor waits for the Shell worker.
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

        // The per-volume setting lives under the volume's GUID. Without it there is no fact to read, so
        // an answer that would otherwise be "enabled" stops at what is actually known.
        var volumeGuid = TryGetVolumeGuid(root);
        if (volumeGuid is null)
        {
            var known = RecycleBinVolumePolicy.Classify(volumeExists: true, driveType, disabledByPolicy, nukeOnDelete: false);

            return new RecycleBinStatus(
                root,
                driveType,
                known == RecycleBinAvailabilityReason.Enabled ? RecycleBinAvailabilityReason.Indeterminate : known);
        }

        var nukeOnDelete = RecycleBinRegistry.IsNukeOnDelete(volumeGuid);

        return new RecycleBinStatus(root, driveType, RecycleBinVolumePolicy.Classify(true, driveType, disabledByPolicy, nukeOnDelete));
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
    /// Reads the volume's GUID path, in the <c>\\?\Volume{…}\</c> form the registry key is named after.
    /// </summary>
    private static string? TryGetVolumeGuid(string root)
    {
        var buffer = new char[GuidPathCapacity];

        if (!PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer))
        {
            return null;
        }

        return new string(buffer).TrimEnd('\\');
    }

    /// <summary>
    /// The volume a path belongs to, always in the form the Shell and the volume APIs accept: a root with
    /// a trailing separator.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetPathRoot(string)"/> answers <c>E:</c> for a bare drive letter, which is a
    /// drive-relative path rather than a root and is rejected by
    /// <c>GetVolumeNameForVolumeMountPoint</c>. Collapsing to a root alone is therefore not enough: the
    /// separator has to be added back.
    /// </remarks>
    /// <param name="volumeRoot">A volume root or any path inside one.</param>
    internal static string NormalizeVolumeRoot(string volumeRoot)
    {
        var root = Path.GetPathRoot(volumeRoot);
        if (string.IsNullOrEmpty(root))
        {
            return AppendSeparator(volumeRoot);
        }

        return root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
    }

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

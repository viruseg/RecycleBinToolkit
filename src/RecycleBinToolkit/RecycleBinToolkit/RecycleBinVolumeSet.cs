using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Chooses the volume roots an operation covers. One rule, both backends.
/// </summary>
/// <remarks>
/// Volume resolution is a question about volumes, not about how an item is moved, so it is asked
/// once here rather than once per backend. A caller who names a path that is not a volume root gets
/// the same refusal whichever backend would have acted on it.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RecycleBinVolumeSet
{
    /// <summary>
    /// Chooses the volumes an operation covers: exactly the one asked for, or every mounted volume
    /// that can hold a Recycle Bin.
    /// </summary>
    /// <returns>
    /// The volume roots to act on. <see langword="null"/> when the caller named a path that is not a
    /// volume root, which is answered as not found rather than quietly widened to the volume containing it.
    /// </returns>
    /// <remarks>
    /// Network drives have no Recycle Bin, so measuring them would report a problem the caller cannot
    /// act on.
    /// <para>
    /// A path inside a volume is refused rather than resolved to that volume. Emptying is not a read:
    /// asking about <c>X:\some\folder</c> and clearing every recycled item on <c>X:\</c> is a much larger
    /// action than the one that was asked for, and a caller who mistyped a root deserves to be told the
    /// path was not one rather than to find out from what disappeared. <see cref="RecycleBin.GetStatusAsync"/>
    /// answers the same input the same way.
    /// </para>
    /// </remarks>
    internal static List<string>? Resolve(string? volumeRoot)
    {
        if (volumeRoot is not null)
        {
            return RecycleBinVolumeState.IsVolumeRoot(volumeRoot)
                ? [RecycleBinVolumeState.NormalizeVolumeRoot(volumeRoot)]
                : null;
        }

        var drives = DriveInfo.GetDrives();
        var roots = new List<string>(drives.Length);

        foreach (var drive in drives)
        {
            if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
            {
                roots.Add(RecycleBinVolumeState.NormalizeVolumeRoot(drive.Name));
            }
        }

        return roots;
    }
}
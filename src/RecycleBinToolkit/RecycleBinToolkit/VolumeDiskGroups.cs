using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The volumes of one physical disk, held by the positions they were passed at, and the disk they share.
/// </summary>
/// <param name="Disk">
/// The disk every volume in the group is a partition of, or <see langword="null"/> when none of them named
/// one. Only that group has a <see langword="null"/> here, and it holds every volume that named no disk.
/// </param>
/// <param name="Positions">
/// Where the group's volumes sit in the list handed to <see cref="VolumeDiskGroups.Group"/>, in the order
/// they were passed. Positions rather than the roots themselves, because the two callers that group hold
/// their own work per volume and neither wants the string back.
/// </param>
/// <remarks>
/// A group is a device. Work spread over two groups touches two devices and does not contend; work inside
/// one group contends for one head, which is why a group's volumes are run in sequence and the groups are
/// run together.
/// </remarks>
internal sealed record VolumeDiskGroup(PhysicalDiskKey? Disk, List<int> Positions);

/// <summary>
/// Gathers volumes by the physical disk they are partitions of, which is the unit work over a machine is
/// split on.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The partition number the driver answers with is left out, and the key is the disk's device type and
/// number rather than the volume itself: two partitions of one disk are the case this exists for, and a key
/// carrying the partition would put every volume in a group of its own.</item>
/// <item><b>Volumes that named no disk are one group, not one group each.</b> A share and a volume whose
/// driver declines are both a volume that could not say where its bytes are, and either may share a head
/// with the other, so the grouping answers the conservative thing: they move one after another.</item>
/// <item>That group names no disk and is appended last whatever order the volumes arrived in, so a caller
/// comparing two runs sees the same shape either way.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class VolumeDiskGroups
{
    /// <summary>
    /// Gathers the volumes by the disk behind them.
    /// </summary>
    /// <param name="volumeRoots">The volumes, in the order the caller named them.</param>
    /// <returns>
    /// One group per disk the volumes named, in the order those disks were first named, and — when any
    /// volume named none — one further group, last, holding every such volume.
    /// </returns>
    /// <remarks>
    /// That is also where a spanning or striped volume can hide: the driver names the storage a volume is a
    /// volume of, so two volumes can key alike while sharing more than one head, and nothing here can see
    /// through the answer to the hardware behind it.
    /// </remarks>
    internal static List<VolumeDiskGroup> Group(IReadOnlyList<string> volumeRoots)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);

        var byDisk = new Dictionary<PhysicalDiskKey, List<int>>();
        var groups = new List<VolumeDiskGroup>();
        List<int>? unnamed = null;

        for (var position = 0; position < volumeRoots.Count; position++)
        {
            if (!PhysicalVolumeDisk.TryIdentify(volumeRoots[position], out var disk))
            {
                unnamed ??= [];
                unnamed.Add(position);
                continue;
            }

            if (!byDisk.TryGetValue(disk, out var positions))
            {
                positions = [];
                byDisk.Add(disk, positions);
                groups.Add(new VolumeDiskGroup(disk, positions));
            }

            positions.Add(position);
        }

        if (unnamed is not null)
        {
            groups.Add(new VolumeDiskGroup(null, unnamed));
        }

        return groups;
    }
}
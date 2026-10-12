using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// What one volume's Recycle Bin holds, as <see cref="RecycleBinContents.Items"/> answers it.
/// </summary>
/// <remarks>
/// A Recycle Bin is one directory per volume, so a listing that covers several volumes has to say which of
/// them each entry came from. That is what this type is: the volume's root, and the entries read from it.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public sealed class RecycleBinVolumeContents
{
    /// <summary>Creates the group for one volume.</summary>
    /// <param name="volumeRoot">
    /// Volume root the entries were read from, such as <c>"X:\\"</c>.
    /// </param>
    /// <param name="items">
    /// The entries that volume's bin holds, in the order its directory gave them. Empty when the volume has
    /// no bin directory yet, or a bin directory holding nothing — both are answers rather than failures, and
    /// a caller that asked about this volume is told so either way.
    /// </param>
    public RecycleBinVolumeContents(string volumeRoot, IReadOnlyList<RecycleBinItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        ArgumentNullException.ThrowIfNull(items);

        VolumeRoot = volumeRoot;
        Items = items;
    }

    /// <summary>Volume root these entries were read from, such as <c>"X:\\"</c>.</summary>
    public string VolumeRoot { get; }

    /// <summary>The entries that volume's bin holds. Empty for a volume whose bin is empty.</summary>
    public IReadOnlyList<RecycleBinItem> Items { get; }

    /// <summary>
    /// The entries of every group in <see cref="RecycleBinContents.Items"/>, one after another.
    /// </summary>
    /// <param name="volumes">The groups to flatten, in the order they should appear.</param>
    /// <returns>
    /// Every entry the groups hold, grouped in that order. A listing whose groups are empty yields an empty
    /// list rather than <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// What <see cref="RecycleBinContents.LoadItemDetails"/> reads: the loader wants one flat list, and
    /// grouping is a property of the answer rather than of the work.
    /// </remarks>
    internal static List<RecycleBinItem> Flatten(IEnumerable<RecycleBinVolumeContents> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        var flat = new List<RecycleBinItem>();

        foreach (var volume in volumes)
        {
            flat.AddRange(volume.Items);
        }

        return flat;
    }
}
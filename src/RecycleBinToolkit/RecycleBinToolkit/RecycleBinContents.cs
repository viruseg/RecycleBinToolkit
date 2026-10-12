using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>The entries read by <see cref="RecycleBin.GetItemsAsync"/>, grouped by volume.</summary>
[SupportedOSPlatform("windows6.0.6000")]
public sealed class RecycleBinContents
{
    /// <summary>Creates a listing, deriving its total from the items.</summary>
    /// <param name="items">The groups that were read, in the order the volumes were asked about.</param>
    /// <param name="failures">Entries or volumes that could not be read.</param>
    /// <param name="elapsed">Time the listing took.</param>
    public RecycleBinContents(
        IReadOnlyList<RecycleBinVolumeContents> items,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed)
    {
        Items = items;
        Failures = failures;
        Elapsed = elapsed;

        long total = 0;
        foreach (var volume in items)
        {
            foreach (var item in volume.Items)
            {
                total += item.Size;
            }
        }

        TotalSize = total;
    }

    /// <summary>
    /// One group per volume that was read, in the order the volumes were asked about. A volume with an empty
    /// bin is one of the groups, holding nothing, so an empty bin and a volume that was never reached do not
    /// look alike.
    /// </summary>
    public IReadOnlyList<RecycleBinVolumeContents> Items { get; }

    /// <summary>Entries that could not be read, at stage <see cref="RecycleFailureStage.Enumeration"/>.</summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Time the listing took.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Sum of <see cref="RecycleBinItem.Size"/> over every group's items.</summary>
    public long TotalSize { get; }

    /// <summary>Reads every item's lazy fields now, so no later access touches the disk.</summary>
    /// <param name="cancellationToken">Cancels the work between records and between volumes.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Items already read are skipped, so nothing is read twice.</item>
    /// <item>An entry whose record cannot be read stays unread and still raises <see cref="IOException"/> on
    /// access. Nothing here throws for one, and <see cref="Failures"/> does not change.</item>
    /// <item>The items are the same instances the listing returned.</item>
    /// <item><b>This method blocks the calling thread</b>, where every other entry point is awaited. Do not
    /// call it on a UI thread.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled. Items read before that point stay read.
    /// </exception>
    public void LoadItemDetails(CancellationToken cancellationToken = default) =>
        ItemDetailLoader.Load(RecycleBinVolumeContents.Flatten(Items), cancellationToken);
}

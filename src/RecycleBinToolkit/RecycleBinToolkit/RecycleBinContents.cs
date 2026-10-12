using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The Recycle Bin contents returned by <see cref="RecycleBin.GetItemsAsync"/>: the entries that were
/// read, grouped by the volume they came from, the ones that were not, and the total size of what was.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
public sealed class RecycleBinContents
{
    /// <summary>Creates a listing, deriving its total from the items.</summary>
    /// <param name="items">
    /// The groups that were read, one per volume, in the order the volumes were asked about.
    /// </param>
    /// <param name="failures">Items or volumes that could not be read.</param>
    /// <param name="elapsed">Wall-clock time spent enumerating.</param>
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
    /// One group per volume that was read, in the order the volumes were asked about. A volume whose bin is
    /// empty is one of the groups, holding nothing.
    /// </summary>
    /// <remarks>
    /// Volumes are read in parallel, and the grouping is what keeps a caller able to tell which disk an entry
    /// came from: the order does not depend on which volume's walk finished first.
    /// <para>
    /// A volume read with nothing in it keeps its place rather than dropping out, because a caller that
    /// asked about <c>X:\</c> and found no group for it could not tell an empty bin from a volume that was
    /// never reached — the same reason a root naming no volume is answered as a failure rather than as an
    /// empty result.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RecycleBinVolumeContents> Items { get; }

    /// <summary>Items or volumes that could not be read.</summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Wall-clock time spent enumerating.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Sum of <see cref="RecycleBinItem.Size"/> over every group's items, derived when the listing was built.</summary>
    public long TotalSize { get; }

    /// <summary>
    /// Reads every item's <see cref="RecycleBinItem.OriginalPath"/> and <see cref="RecycleBinItem.DeletedAt"/>
    /// now, so that no later property access touches the disk.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work between records and between volumes.</param>
    /// <remarks>
    /// A listing reads no records at all — that is what makes one walk
    /// of the bin enough, however many entries there are — and each item reads its own on first use. This is for
    /// a caller about to read all of them: it moves that cost into one batch, in parallel, instead of leaving it
    /// spread through a loop that also has to do something else.
    /// <para>
    /// The items already read are skipped, which is every item of a listing
    /// taken with <see cref="RecycleBinOptions.PreloadItemDetails"/>. Nothing is read twice, and a listing
    /// with nothing pending does no work at all beyond one pass over its items.
    /// </para>
    /// <para>
    /// An item whose record cannot be read is left exactly as it was: it still raises <see cref="IOException"/>
    /// when a caller asks for its details, with the filesystem's own code. Nothing here throws for one, and
    /// <see cref="Failures"/> does not change — it describes what the listing could not do, not what a later
    /// call could not.
    /// </para>
    /// <para>
    /// Every volume's groups are flattened into one list first, and the volumes are then read one after
    /// another, and each on as many threads as its own hardware answers for: one for a
    /// mechanical disk, more for a solid-state one, one for a share. Reading the listing itself does not
    /// work that way — it reads the volumes together — but this warms what the listing left cold, and
    /// warming measures each disk on its own figure.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work finished. Items read before that point
    /// stay read.
    /// </exception>
    public void LoadItemDetails(CancellationToken cancellationToken = default) =>
        ItemDetailLoader.Load(RecycleBinVolumeContents.Flatten(Items), cancellationToken);
}

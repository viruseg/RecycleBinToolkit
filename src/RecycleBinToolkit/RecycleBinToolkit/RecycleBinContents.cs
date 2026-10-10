using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The Recycle Bin contents returned by <see cref="RecycleBin.GetItemsAsync"/>: the entries that were
/// read, the ones that were not, and the total size of what was.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
public sealed class RecycleBinContents
{
    /// <summary>Creates a listing, deriving its total from the items.</summary>
    /// <param name="items">The items that were read.</param>
    /// <param name="failures">Items or volumes that could not be read.</param>
    /// <param name="elapsed">Wall-clock time spent enumerating.</param>
    public RecycleBinContents(
        IReadOnlyList<RecycleBinItem> items,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed)
    {
        Items = items;
        Failures = failures;
        Elapsed = elapsed;

        long total = 0;
        foreach (var item in items)
        {
            total += item.Size;
        }

        TotalSize = total;
    }

    /// <summary>The items that were read.</summary>
    public IReadOnlyList<RecycleBinItem> Items { get; }

    /// <summary>Items or volumes that could not be read.</summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Wall-clock time spent enumerating.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Sum of <see cref="RecycleBinItem.Size"/> over <see cref="Items"/>, derived when the listing was built.</summary>
    public long TotalSize { get; }

    /// <summary>
    /// Reads every item's <see cref="RecycleBinItem.OriginalPath"/> and <see cref="RecycleBinItem.DeletedAt"/>
    /// now, so that no later property access touches the disk.
    /// </summary>
    /// <param name="cancellationToken">Cancels the work between records and between volumes.</param>
    /// <remarks>
    /// A listing on <see cref="RecycleBinBackend.Native"/> reads no records at all — that is what makes one walk
    /// of the bin enough, however many entries there are — and each item reads its own on first use. This is for
    /// a caller about to read all of them: it moves that cost into one batch, in parallel, instead of leaving it
    /// spread through a loop that also has to do something else.
    /// <para>
    /// The items already read are skipped, which is every item of a Shell listing and every item of a listing
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
    /// Volumes are read one after another, and each on as many threads as its own hardware answers for: one for a
    /// mechanical disk, more for a solid-state one, one for a share.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work finished. Items read before that point
    /// stay read.
    /// </exception>
    public void LoadItemDetails(CancellationToken cancellationToken = default) =>
        ItemDetailLoader.Load(Items, cancellationToken);
}

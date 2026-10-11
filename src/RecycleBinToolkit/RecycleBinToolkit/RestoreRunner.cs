using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves entries out of the Recycle Bin and back to the paths they were deleted from.
/// </summary>
/// <remarks>
/// Synchronous, because everything it does blocks: a file move is a metadata operation the thread waits on.
/// It is only ever given a thread from the pool, never a caller's.
/// <para>
/// <b>The order of the two actions is the whole design, and it is not interchangeable.</b> The payload is
/// moved out first and the <c>$I</c> record is deleted second. The other order — record first, payload
/// second — leaves a payload in <c>$RECYCLE.BIN</c> that nothing describes when the move fails:
/// <see cref="NativeBinEntries"/> cannot pair the two halves, no listing shows it, and nothing above this
/// library can put it back, because a restore needs a record to say where a payload came from. Only
/// <see cref="NativeBinEmptier"/> would ever collect it, and a caller who never empties their bin has lost
/// the file. The chosen order's failure is the opposite shape: the file is already back where its caller
/// expects it while its entry still shows in the bin — visible in a listing, reported by the result, and
/// fixable by restoring that entry again.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RestoreRunner
{
    /// <summary>
    /// Puts every entry back and reports what happened to each.
    /// </summary>
    /// <param name="items">
    /// The entries to restore, as a listing returned them. Their order is the order the answer is reported
    /// in.
    /// </param>
    /// <param name="options">
    /// Settings, normalised here rather than trusted: a caller-supplied <see cref="RecycleBinOptions"/> is
    /// not obliged to have been through <see cref="RecycleBinOptions.Normalize"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation between entries.</param>
    /// <returns>
    /// What every entry became. The elapsed time is zero because the caller measures the wait instead.
    /// </returns>
    /// <remarks>
    /// <b>Each entry's outcome goes into a slot taken by its own position in the caller's list, and the
    /// lists are flattened in that order once every volume is done.</b> That is what
    /// <see cref="NativeBinLister"/> does with the volumes it reads, and the reason is the same one: the
    /// order work finished in is not an order a caller can rely on, and an answer that moved with the
    /// machine's load could not be compared with the next call's.
    /// <para>
    /// A volume's entries are read and moved together, and one volume is finished before the next is
    /// begun — which is the order <see cref="ItemDetailLoader"/> already reads volumes in. Reading is the
    /// volume's own measured parallelism, because that is the part of the work a disk's hardware answers
    /// for; moving is one entry at a time, because how many moves a disk takes well is a separate question
    /// that is measured rather than borrowed from the reading figures.
    /// </para>
    /// </remarks>
    internal static RecycleBinRestoreResult Run(
        IReadOnlyList<RecycleBinItem> items,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        options = options.Normalize();

        // One slot per entry, in the caller's order. A null slot is an entry nothing has been decided for.
        var slots = new RestoreOutcome?[items.Count];

        foreach (var volume in GroupByVolume(items, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RestoreVolume(items, volume.Value, options, slots, cancellationToken);
        }

        return Flatten(slots);
    }

    /// <summary>
    /// Restores one volume's entries, in the order the caller passed them.
    /// </summary>
    /// <param name="items">The caller's entries, which is where a position is resolved into an entry.</param>
    /// <param name="positions">
    /// Where those entries sat in the caller's list. Carried rather than looked up: an entry answers for its
    /// own identity rather than for its place, and a hash of every <c>$I</c> path on a bin of tens of
    /// thousands buys nothing a number taken once does not.
    /// </param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="slots">Where each entry's outcome is written, by position in the caller's list.</param>
    /// <param name="cancellationToken">Cancels the operation between entries.</param>
    /// <remarks>
    /// Records are read before anything moves, through <see cref="ItemDetailLoader"/>, so a move is never
    /// the thing that discovers where the entry came from. A listing taken with
    /// <see cref="RecycleBinOptions.PreloadItemDetails"/> left its entries already read, and the loader
    /// reads nothing for those again — which is why they cost one field read each here and a file read
    /// each in a cold listing.
    /// </remarks>
    private static void RestoreVolume(
        IReadOnlyList<RecycleBinItem> items,
        List<int> positions,
        RecycleBinOptions options,
        RestoreOutcome?[] slots,
        CancellationToken cancellationToken)
    {
        var entries = new List<RecycleBinItem>(positions.Count);

        for (var i = 0; i < positions.Count; i++)
        {
            entries.Add(items[positions[i]]);
        }

        ItemDetailLoader.Load(entries, cancellationToken);

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Move(entries[i], options, slots, positions[i]);
        }
    }

    /// <summary>
    /// Puts one entry back where it came from, and records what became of it in the slot its position in
    /// the caller's list gave it.
    /// </summary>
    /// <param name="item">The entry, whose record has been read.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="slots">Where the outcome is written, by position in the caller's list.</param>
    /// <param name="position">That entry's position in the caller's list.</param>
    /// <remarks>
    /// What is in the way is asked about rather than assumed, because it is a fact about the disk and not
    /// about this call: an entry whose path was taken again while it sat in the Recycle Bin must not be
    /// moved over whatever is there now. Only <see cref="RestoreAction.Move"/> is acted on here — an entry
    /// the caller was not going to put back keeps its record, keeps its place in the bin, and is reported
    /// by whatever the conflict policies settle, which is a decision this method does not make.
    /// </remarks>
    private static void Move(
        RecycleBinItem item,
        RecycleBinOptions options,
        RestoreOutcome?[] slots,
        int position)
    {
        var decision = RestorePathPlanner.Decide(item.OriginalPath, item.IsDirectory, options);

        if (decision.Action != RestoreAction.Move)
        {
            return;
        }

        // Payload first, record second, for the reason this class is documented with.
        File.Move(item.PayloadPath, decision.TargetPath);
        File.Delete(item.IndexPath);

        slots[position] = new RestoreOutcome(decision.TargetPath, item.Size);
    }

    /// <summary>
    /// Where each entry sits in the caller's list, under the volume its record lives on.
    /// </summary>
    /// <param name="items">The caller's entries, in their order.</param>
    /// <param name="cancellationToken">Cancels the grouping between entries.</param>
    /// <returns>
    /// One list of positions per volume, in the order the volumes first appear among the entries and each
    /// list in the caller's own order.
    /// </returns>
    /// <remarks>
    /// Keyed without regard to case, as the root itself is: Windows writes <c>E:\</c> and <c>e:\</c> for the
    /// same volume, and the two spellings would otherwise have the same disk read twice under two policies.
    /// The root is a span over the entry's own path and is only turned into a string when a volume is seen
    /// for the first time — the alternative is one string per entry to hold a root that is three characters
    /// long.
    /// </remarks>
    private static Dictionary<string, List<int>> GroupByVolume(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var volumes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var lookup = volumes.GetAlternateLookup<ReadOnlySpan<char>>();

        for (var position = 0; position < items.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var root = items[position].VolumeRoot;

            if (!lookup.TryGetValue(root, out var group))
            {
                group = [];
                volumes.Add(root.ToString(), group);
                lookup = volumes.GetAlternateLookup<ReadOnlySpan<char>>();
            }

            group.Add(position);
        }

        return volumes;
    }

    /// <summary>
    /// What one entry became, held in the slot its position in the caller's list gave it until every volume
    /// is done.
    /// </summary>
    /// <param name="RestoredPath">The path the entry was put back at, in full.</param>
    /// <param name="Size">The bytes it carried back, as its entry measured them.</param>
    private readonly record struct RestoreOutcome(string RestoredPath, long Size);

    /// <summary>
    /// Reads the slots in the caller's order and reports what they add up to.
    /// </summary>
    private static RecycleBinRestoreResult Flatten(RestoreOutcome?[] slots)
    {
        var restoredPaths = new List<string>(slots.Length);
        long itemsRestored = 0;
        long restoredSize = 0;

        foreach (var slot in slots)
        {
            if (slot is { } restored)
            {
                restoredPaths.Add(restored.RestoredPath);
                itemsRestored++;
                restoredSize += restored.Size;
            }
        }

        return new RecycleBinRestoreResult(restoredPaths, [], [], itemsRestored, restoredSize, TimeSpan.Zero);
    }
}
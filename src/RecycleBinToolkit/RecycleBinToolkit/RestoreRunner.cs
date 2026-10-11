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

        // One slot per entry, in the caller's order. A slot still carrying its opening kind is an entry
        // nothing has been decided for.
        var slots = new RestoreOutcome[items.Count];

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
        RestoreOutcome[] slots,
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

            Restore(entries[i], options, slots, positions[i]);
        }
    }

    /// <summary>
    /// Decides what to do about one entry, does it, and records what became of it in the slot its position in
    /// the caller's list gave it.
    /// </summary>
    /// <param name="item">The entry, whose record has been read.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="slots">Where the outcome is written, by position in the caller's list.</param>
    /// <param name="position">That entry's position in the caller's list.</param>
    /// <remarks>
    /// What is in the way is asked about rather than assumed, because it is a fact about the disk and not
    /// about this call: an entry whose path was taken again while it sat in the Recycle Bin must not be
    /// moved over whatever is there now. Every answer the planner gives is acted on here, and each is a
    /// whole answer — nothing outside this switch reaches the payload, and nothing here renames, moves or
    /// removes what is already on disk except under <see cref="RestoreAction.Replace"/>, which the caller
    /// asked for by name.
    /// <para>
    /// An entry that is not moved keeps its record and its place in the bin, which is what leaves a second
    /// attempt worth making. Whether that is a conflict the caller asked about or a failure is the answer in
    /// the options rather than in the action: both <c>Error</c> policies answer
    /// <see cref="RestoreAction.Skip"/>, and only one of them also lands in
    /// <see cref="RecycleBinRestoreResult.Failures"/>.
    /// </para>
    /// </remarks>
    private static void Restore(
        RecycleBinItem item,
        RecycleBinOptions options,
        RestoreOutcome[] slots,
        int position)
    {
        var decision = RestorePathPlanner.Decide(item.OriginalPath, item.IsDirectory, options);

        switch (decision.Action)
        {
            case RestoreAction.Move:
                Place(item, decision.TargetPath);
                slots[position] = new RestoreOutcome(RestoreOutcomeKind.Restored, decision.TargetPath, item.Size);
                break;

            case RestoreAction.Replace:
                Place(item, decision.TargetPath, overwrite: true);
                slots[position] = new RestoreOutcome(RestoreOutcomeKind.Replaced, decision.TargetPath, item.Size);
                break;

            case RestoreAction.Rename:
                Place(item, decision.TargetPath);
                slots[position] = new RestoreOutcome(RestoreOutcomeKind.RestoredRenamed, decision.TargetPath, item.Size)
                {
                    OccupiedByDirectory = decision.OccupiedByDirectory
                };
                break;

            default:
                slots[position] = new RestoreOutcome(
                    IsRefusal(item, options) ? RestoreOutcomeKind.Refused : RestoreOutcomeKind.Skipped,
                    decision.TargetPath,
                    item.Size)
                {
                    OccupiedByDirectory = decision.OccupiedByDirectory
                };
                break;
        }
    }

    /// <summary>
    /// Puts the payload down at <paramref name="targetPath"/> and takes the record away, in that order.
    /// </summary>
    /// <param name="item">The entry whose payload moves and whose record goes.</param>
    /// <param name="targetPath">Where the payload is to land.</param>
    /// <param name="overwrite">
    /// Whether what is at <paramref name="targetPath"/> is to be overwritten. <see langword="false"/> is what
    /// a move to a free path is, and asking for it costs nothing over the answer that would refuse.
    /// </param>
    /// <remarks>
    /// One place does the move and the delete, because the order between them is the whole design and is
    /// the reason this class is documented the way it is. A branch that wanted them the other way round
    /// would have to be argued for in its own right.
    /// </remarks>
    private static void Place(RecycleBinItem item, string targetPath, bool overwrite = false)
    {
        // Payload first, record second, for the reason this class is documented with.
        File.Move(item.PayloadPath, targetPath, overwrite);
        File.Delete(item.IndexPath);
    }

    /// <summary>
    /// Whether an occupied path is a conflict to resolve under the policy in force, or a refusal the caller
    /// asked to be told about as a failure.
    /// </summary>
    /// <param name="item">The entry whose destination is occupied.</param>
    /// <param name="options">The normalised settings the restore is running under.</param>
    /// <returns>
    /// Whether the entry should land in <see cref="RecycleBinRestoreResult.Failures"/> as well as in
    /// <see cref="RecycleBinRestoreResult.Conflicts"/>.
    /// </returns>
    /// <remarks>
    /// The policy asked about is the one for the kind arriving, because the planner answered the occupied
    /// path out of that same one: a file is refused or skipped by
    /// <see cref="RecycleBinOptions.FileConflict"/> whatever occupies its path, and a directory by
    /// <see cref="RecycleBinOptions.DirectoryConflict"/>.
    /// </remarks>
    private static bool IsRefusal(RecycleBinItem item, RecycleBinOptions options) =>
        item.IsDirectory
            ? options.DirectoryConflict == RestoreDirectoryConflict.Error
            : options.FileConflict == RestoreFileConflict.Error;

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
    /// What one entry became, which is what decides the lists its slot is flattened into.
    /// </summary>
    private enum RestoreOutcomeKind
    {
        /// <summary>The opening kind: nothing has been decided for this entry.</summary>
        Undecided = 0,

        /// <summary>
        /// The entry went back to a path nothing was using, which is the whole of a restore that hit
        /// nothing.
        /// </summary>
        Restored,

        /// <summary>
        /// The entry took the occupant's place. An overwrite resolves the conflict by itself, so it leaves
        /// nothing to report beyond the path — which is what makes this a kind of its own rather than the
        /// one above it: the entry in the bin is gone and no record says anything was in the way.
        /// </summary>
        Replaced,

        /// <summary>
        /// The entry was set down under a free name beside the one it wanted. It is a restored path as
        /// well as a conflict, and the conflict names the name it landed under rather than the one it
        /// collided at.
        /// </summary>
        RestoredRenamed,

        /// <summary>
        /// The entry stays in the bin and the caller asked for that to be a conflict to read rather than a
        /// failure, so nothing failed.
        /// </summary>
        Skipped,

        /// <summary>
        /// The entry stays in the bin and the caller asked for that to be a failure, so it is reported as
        /// one as well as as a conflict — the only way a caller reading the conflicts alone could tell it
        /// from a skip.
        /// </summary>
        Refused
    }

    /// <summary>
    /// What one entry became, held in the slot its position in the caller's list gave it until every volume
    /// is done.
    /// </summary>
    /// <param name="Kind">What became of the entry, which is what decides the lists it reaches.</param>
    /// <param name="Path">
    /// The path the entry is to be reported at: where it landed, or — for an entry that was not moved — the
    /// path that was in its way. The two are one string for every kind but
    /// <see cref="RestoreOutcomeKind.RestoredRenamed"/>, because the planner lands every other action on the
    /// path the entry asked for.
    /// </param>
    /// <param name="Size">The bytes the entry carries back, as its own record measured them.</param>
    private readonly record struct RestoreOutcome(RestoreOutcomeKind Kind, string Path, long Size)
    {
        /// <summary>Whether what occupies the path the entry wanted is a directory rather than a file.</summary>
        internal bool OccupiedByDirectory { get; init; }
    }

    /// <summary>
    /// Reads the slots in the caller's order and reports what they add up to.
    /// </summary>
    /// <remarks>
    /// Each slot goes to the lists its kind names, and to two of them where two is the answer the caller
    /// asked for. The switch is the whole of the mapping rather than something spread through the loop, so
    /// a kind with no arm is reported nowhere instead of in the wrong list.
    /// </remarks>
    private static RecycleBinRestoreResult Flatten(RestoreOutcome[] slots)
    {
        var restoredPaths = new List<string>(slots.Length);
        var conflicts = new List<RecycleRestoreConflict>();
        var failures = new List<RecycleFailure>();
        long itemsRestored = 0;
        long restoredSize = 0;

        foreach (var slot in slots)
        {
            switch (slot.Kind)
            {
                case RestoreOutcomeKind.Restored:
                case RestoreOutcomeKind.Replaced:
                    restoredPaths.Add(slot.Path);
                    itemsRestored++;
                    restoredSize += slot.Size;
                    break;

                case RestoreOutcomeKind.RestoredRenamed:
                    restoredPaths.Add(slot.Path);
                    itemsRestored++;
                    restoredSize += slot.Size;
                    conflicts.Add(new RecycleRestoreConflict(slot.Path, slot.OccupiedByDirectory, RecycleRestoreConflictAction.RestoredRenamed));
                    break;

                case RestoreOutcomeKind.Skipped:
                    conflicts.Add(new RecycleRestoreConflict(slot.Path, slot.OccupiedByDirectory, RecycleRestoreConflictAction.Skipped));
                    break;

                case RestoreOutcomeKind.Refused:
                    conflicts.Add(new RecycleRestoreConflict(slot.Path, slot.OccupiedByDirectory, RecycleRestoreConflictAction.Skipped));
                    failures.Add(new RecycleFailure(slot.Path, 0, 0, RecycleFailureStage.RestoreConflictRefused));
                    break;
            }
        }

        return new RecycleBinRestoreResult(restoredPaths, conflicts, failures, itemsRestored, restoredSize, TimeSpan.Zero);
    }
}
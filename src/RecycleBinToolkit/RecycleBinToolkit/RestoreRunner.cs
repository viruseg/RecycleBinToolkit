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
/// <para>
/// <b>Nothing one entry does ends the call.</b> A payload deleted since the listing, a destination occupied by
/// the other kind, a record that cannot be read, an entry the caller named twice: each is one entry's answer,
/// carried in <see cref="RecycleBinRestoreResult.Failures"/> with the code the filesystem gave where there was
/// one, and the entries around it keep the outcomes they had. A caller restoring ninety entries gets ninety
/// answers however many of them the disk would not allow, which is the only shape in which the failures list
/// can be read as a list rather than as the exception that stopped the batch.
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

        // Every entry this call has already answered, so that a list naming one twice moves it once. Keyed by
        // the entry's own identity — its $I path — which is one string comparison and reads no record.
        var settled = new HashSet<RecycleBinItem>();

        foreach (var volume in GroupByVolume(items, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RestoreVolume(items, volume.Value, options, slots, settled, cancellationToken);
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
    /// <param name="settled">The entries this call has already answered, carried across volumes so that a list naming one twice answers it once.</param>
    /// <param name="cancellationToken">Cancels the operation between entries.</param>
    /// <remarks>
    /// Records are read before anything moves, through <see cref="ItemDetailLoader"/>, so a move is never
    /// the thing that discovers where the entry came from. A listing taken with
    /// <see cref="RecycleBinOptions.PreloadItemDetails"/> left its entries already read, and the loader
    /// reads nothing for those again — which is why they cost one field read each here and a file read
    /// each in a cold listing.
    /// <para>
    /// One volume holds every copy of one entry: an entry's volume is a prefix of its own record path, so two
    /// equal entries are answered in the same group and the first of them really does come first.
    /// </para>
    /// </remarks>
    private static void RestoreVolume(
        IReadOnlyList<RecycleBinItem> items,
        List<int> positions,
        RecycleBinOptions options,
        RestoreOutcome[] slots,
        HashSet<RecycleBinItem> settled,
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

            Restore(entries[i], options, slots, settled, positions[i]);
        }
    }

    /// <summary>
    /// Decides what to do about one entry, does it, and records what became of it in the slot its position in
    /// the caller's list gave it.
    /// </summary>
    /// <param name="item">The entry, whose record has been read.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="slots">Where the outcome is written, by position in the caller's list.</param>
    /// <param name="settled">The entries this call has already answered.</param>
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
    /// <para>
    /// <b>What goes wrong here is an answer rather than an exception.</b> An entry the caller named twice is
    /// reported the second time and not moved, because the first has already been answered and a second
    /// would move one payload twice or report one collision twice. And a refusal from the filesystem — a
    /// payload deleted since the listing, a destination occupied by the other kind under a policy that asked
    /// to overwrite it, a record that cannot be read — becomes a
    /// <see cref="RecycleFailureStage.RestoreFailed"/> carrying the code it raised, whichever policy asked
    /// for the move. Only what the filesystem itself raises on these paths is caught, so a defect here still
    /// surfaces rather than being reported as a disk that said no.
    /// </para>
    /// </remarks>
    private static void Restore(
        RecycleBinItem item,
        RecycleBinOptions options,
        RestoreOutcome[] slots,
        HashSet<RecycleBinItem> settled,
        int position)
    {
        // Hoisted so that a refusal on the way to a decision can still be reported against the path the entry
        // came from. Reading the record and deciding where to put it are one try because one of them being the
        // thing that raises is not worth the caller losing the other's answer, but only the record read leaves
        // this null, and null is what says there was no path to name.
        string? originalPath = null;

        RestoreDecision decision;

        try
        {
            originalPath = item.OriginalPath;

            // Answered before the destination is looked at, which is the only order in which the answer is the
            // truth: the first of two identical entries has already been answered by the time the second is
            // reached, and asking the planner about that would move one payload twice or report one collision
            // twice.
            if (!settled.Add(item))
            {
                slots[position] = Failed(originalPath, item.Size, hResult: 0, attempts: 0);
                return;
            }

            decision = RestorePathPlanner.Decide(originalPath, item.IsDirectory, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing was placed, and the path named is the one the entry came from — which the planner never
            // got to use and the caller needs to see. Only a record that could not be read leaves nothing to
            // name, because that record is the one thing saying where the entry was deleted from, and its own
            // $I path is what is left.
            slots[position] = Failed(originalPath ?? item.IndexPath, item.Size, ex.HResult, attempts: 0);
            return;
        }

        try
        {
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The move, or the delete of a record whose payload has already been put back, is what raised. The
            // second of those is why this is reported and not acted on: the file is already where its caller
            // wanted it, and the entry is left in the bin to be recognised as restored next time.
            slots[position] = Failed(decision.TargetPath, item.Size, ex.HResult, attempts: 1);
        }
    }

    /// <summary>
    /// An entry that did not come back, under the path it is to be reported at and the code the filesystem
    /// gave for it.
    /// </summary>
    /// <param name="path">The path the failure names: where the entry was to land, or — where nothing could be read about where it came from — its own record.</param>
    /// <param name="size">The bytes the entry carries, as its record measured them.</param>
    /// <param name="hResult">The code the filesystem raised, or <c>0</c> where nothing was asked of it.</param>
    /// <param name="attempts">How many times the payload was put down for this entry before it was given up on.</param>
    private static RestoreOutcome Failed(string path, long size, int hResult, int attempts) =>
        new(RestoreOutcomeKind.Failed, path, size)
        {
            HResult = hResult,
            Attempts = attempts
        };

    /// <summary>
    /// Puts the payload down at <paramref name="targetPath"/> and takes the record away, in that order.
    /// </summary>
    /// <param name="item">The entry whose payload moves and whose record goes.</param>
    /// <param name="targetPath">Where the payload is to land.</param>
    /// <param name="overwrite">
    /// Whether what is at <paramref name="targetPath"/> is to be overwritten. <see langword="false"/> is what
    /// a move to a free path is, and asking for it costs nothing over the answer that would refuse. It is
    /// never set for a directory, which <see cref="RestorePathPlanner.Decide"/> never answers
    /// <see cref="RestoreAction.Replace"/> for.
    /// </param>
    /// <remarks>
    /// One place does the move and the delete, because the order between them is the whole design and is
    /// the reason this class is documented the way it is. A branch that wanted them the other way round
    /// would have to be argued for in its own right.
    /// <para>
    /// A directory is one <see cref="Directory.Move(string, string)"/> rather than a walk of what is inside
    /// it. Windows moves a whole directory to a free name in a single call, and that is the shape nearly
    /// every directory restore takes; a destination something is already standing in is not a name that
    /// call can land on.
    /// </para>
    /// <para>
    /// The folder the payload lands in is made here rather than by the caller, because the folder
    /// <see cref="RecycleBinItem.OriginalPath"/> sits in need not be there any more and neither move takes
    /// a path whose own parent is missing. Belonging here rather than one level up is also what keeps an
    /// entry that is not going to be placed from leaving a folder tree behind: nothing reaches this method
    /// unless the entry is being put down.
    /// </para>
    /// <para>
    /// One path is resolved once, without its trailing separator, and both the folder taken off it and the
    /// move itself are given that. A record written from a path a caller spelled as a directory carries
    /// the separator, and it is read as the same name by everything here rather than by half of it.
    /// </para>
    /// </remarks>
    private static void Place(RecycleBinItem item, string targetPath, bool overwrite = false)
    {
        // The one place a destination is resolved, so the folder made below and the move onto it cannot answer
        // about two different names of the same place.
        var destination = Path.TrimEndingDirectorySeparator(targetPath);

        CreateTheParentFolder(destination);

        if (item.IsDirectory)
        {
            Directory.Move(item.PayloadPath, destination);
        }
        else
        {
            File.Move(item.PayloadPath, destination, overwrite);
        }

        // Payload first, record second, for the reason this class is documented with.
        File.Delete(item.IndexPath);
    }

    /// <summary>
    /// Creates whatever is missing of the chain of folders ending at the parent of <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The path a payload is about to land at, without a trailing separator.</param>
    /// <remarks>
    /// A record names the path a thing was deleted from and says nothing about the folders on the way to
    /// it. Somebody who empties a folder after deleting from it is the ordinary way of getting one whose
    /// parent is gone, and the whole chain has to be made: a move into a directory that is not there is a
    /// path that is not there. The trimming is the caller's, so that the folder made here and the path
    /// moved to are two readings of one string rather than of two spellings of one name.
    /// </remarks>
    private static void CreateTheParentFolder(string path)
    {
        var parent = Path.GetDirectoryName(path);

        // A path that is a volume root, or one written without any directory part, has no parent to make.
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
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
        /// The entry took the occupant's place, overwriting what was there.
        /// </summary>
        /// <remarks>
        /// Carried as a kind of its own to name the event rather than to change the output: an overwrite
        /// resolves its own conflict, so this reaches exactly the lists <see cref="Restored"/> does and
        /// leaves no record that anything was in the way.
        /// </remarks>
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
        Refused,

        /// <summary>
        /// The filesystem refused to put the entry back, or would not say where it came from, and the refusal
        /// is carried with the code it raised.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Refused"/> in that nothing was in the way and nothing was chosen: this is
        /// what a payload deleted since the listing, a destination holding the other kind under a policy that
        /// asked to overwrite it, and a record that cannot be read all arrive as. The stage is the same either
        /// way — <see cref="RecycleFailureStage.RestoreFailed"/> is where a restore that did not happen is
        /// reported, and <see cref="RecycleFailureStage.RestoreConflictRefused"/> is where a conflict the
        /// caller asked to be told about is.
        /// </remarks>
        Failed
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
    /// path the entry asked for. A failure names the path the action would have used, which is the path the
    /// entry came from; where that could not be read there is no such path to name, and the entry's own record
    /// inside the Recycle Bin is named instead.
    /// </param>
    /// <param name="Size">The bytes the entry carries back, as its own record measured them.</param>
    private readonly record struct RestoreOutcome(RestoreOutcomeKind Kind, string Path, long Size)
    {
        /// <summary>Whether what occupies the path the entry wanted is a directory rather than a file.</summary>
        internal bool OccupiedByDirectory { get; init; }

        /// <summary>
        /// The code the filesystem raised for this entry, or <c>0</c> when nothing was asked of it.
        /// </summary>
        /// <remarks>
        /// Carried through exactly as the exception carried it, because <see cref="RecycleFailure.HResult"/>
        /// promises a caller the same code. A kind that never failed never sets it, and neither does a failure
        /// nothing was attempted for.
        /// </remarks>
        internal int HResult { get; init; }

        /// <summary>
        /// How many times the payload was put down for this entry before it was given up on, which is
        /// <c>0</c> for every kind but <see cref="RestoreOutcomeKind.Failed"/> and for a failure nothing was
        /// attempted for.
        /// </summary>
        internal int Attempts { get; init; }
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

                case RestoreOutcomeKind.Failed:
                    failures.Add(new RecycleFailure(slot.Path, slot.HResult, slot.Attempts, RecycleFailureStage.RestoreFailed));
                    break;
            }
        }

        return new RecycleBinRestoreResult(restoredPaths, conflicts, failures, itemsRestored, restoredSize, TimeSpan.Zero);
    }
}
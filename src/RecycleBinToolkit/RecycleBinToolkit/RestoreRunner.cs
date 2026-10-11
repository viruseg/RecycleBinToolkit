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
/// <b>Nothing one entry does ends the call; the caller's own cancellation does.</b> A payload deleted since the
/// listing, a destination occupied by the other kind, a record that cannot be read, an entry the caller named
/// twice: each is one entry's answer, carried in <see cref="RecycleBinRestoreResult.Failures"/> with the code
/// the filesystem gave where there was one, and the entries around it keep the outcomes they had. A caller
/// restoring ninety entries gets ninety answers however many of them the disk would not allow, which is the
/// only shape in which the failures list can be read as a list rather than as the exception that stopped the
/// batch.
/// </para>
/// <para>
/// A cancellation is not one of those answers. It raises <see cref="OperationCanceledException"/> out of
/// <see cref="Run"/> and returns nothing, because a restore is not transactional and so what it got through
/// is found by listing the bin — the same answer
/// <see cref="RecycleBin.RecycleAsync(IReadOnlyList{string?}, RecycleBinOptions?, CancellationToken)"/>
/// gives for its own cancellation.
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
    /// <param name="cancellationToken">
    /// Cancels the operation between entries and between volumes. A cancellation raises
    /// <see cref="OperationCanceledException"/> rather than returning a result, and the entries already put
    /// back are left where they are.
    /// </param>
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
    /// <para>
    /// <b>The slots are what a cancellation throws away.</b> A caller who cancels is told the call stopped,
    /// and the entries it had not reached are in the bin with their records still beside their payloads.
    /// <para>
    /// A merge is the exception, and the one that has moved something: its walk has already set down whatever
    /// children it placed before the cancellation reached it, and the payload holds the rest. Nothing rolls any
    /// of that back, and no slot is written for the entry either — so nothing reads the slots to say what went,
    /// and the caller who wants that reads the bin.
    /// </para>
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

        // The one slot a merge's put-back is written behind. A Recycle Bin is one directory per volume and
        // every record lands in it, so writers in it contend: recycling 3,000 records measured 542 files a
        // second on one thread against 189 on eight. Nothing here runs in parallel and so nothing contends
        // for this; it is taken because the put-back is the one part of a restore that writes into that
        // directory, and a walk that gained threads would need it to be one guard per physical disk rather
        // than one for the call.
        using var putBack = new SemaphoreSlim(1, 1);

        foreach (var volume in GroupByVolume(items, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RestoreVolume(items, volume.Value, options, slots, settled, putBack, cancellationToken);
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
    /// <param name="putBack">The guard a merge writes its put-back behind.</param>
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
        SemaphoreSlim putBack,
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

            Restore(entries[i], options, slots, settled, putBack, positions[i], cancellationToken);
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
    /// <param name="putBack">The guard a merge writes its put-back behind.</param>
    /// <param name="position">That entry's position in the caller's list.</param>
    /// <param name="cancellationToken">Cancels the operation between this entry and the next.</param>
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
        SemaphoreSlim putBack,
        int position,
        CancellationToken cancellationToken)
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
                case RestoreAction.Move when Merges(item, decision, options):
                    slots[position] = Merge(item, decision.TargetPath, options, putBack, cancellationToken);
                    break;

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
            //
// This filter must never gain an OperationCanceledException clause. The switch above calls Merge,
            // which reaches PutBack, and that hands the token to a caller which raises it — so a live
            // cancellation raises inside this very try. Reported here it would be this entry's failure
            // rather than the caller's own cancellation, and the raise would go no further.
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
    /// Whether this decision is a merge: a directory arriving at a directory that is already there, under
    /// one of the three policies that ask to be merged rather than to take a name of its own.
    /// </summary>
    /// <param name="item">The entry, whose record has been read.</param>
    /// <param name="decision">What the planner answered for it.</param>
    /// <param name="options">The normalised settings the restore is running under.</param>
    /// <returns>
    /// <see langword="true"/> when the destination is occupied by a directory and the caller asked for the
    /// tree to be merged into it.
    /// </returns>
    /// <remarks>
    /// Both halves are needed and neither answers for the other. The occupant's kind is what separates a
    /// merge from a directory arriving at a <em>file</em>, which
    /// <see cref="RestorePathPlanner.Decide"/> answers with the same <see cref="RestoreAction.Move"/> under
    /// every merge value: there is no tree to merge into, and the move is refused. The policy is the third,
    /// and the planner already implies it — among occupied destinations it answers
    /// <see cref="RestoreAction.Move"/> for the three merge values and for nothing else. It is asked anyway
    /// because a value from outside the set is answered as a skip everywhere else in this library, and a
    /// walk driven by one would put nothing anywhere.
    /// </remarks>
    private static bool Merges(RecycleBinItem item, RestoreDecision decision, RecycleBinOptions options) =>
        item.IsDirectory
            && decision.OccupiedByDirectory
            && options.DirectoryConflict is RestoreDirectoryConflict.MergeSkip
                or RestoreDirectoryConflict.MergeReplace
                or RestoreDirectoryConflict.MergeRename;

    /// <summary>
    /// Puts a directory's tree into the directory already standing at its path, and takes the entry's own
    /// record away once everything inside the payload has been dealt with.
    /// </summary>
    /// <param name="item">The entry, whose record has been read.</param>
    /// <param name="targetPath">The path the record named, which is a directory that is already there.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="putBack">The guard the put-back is written behind.</param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <returns>
    /// What became of the entry. A merge that could not finish is
    /// <see cref="RestoreOutcomeKind.Failed"/> whether it failed in the walk or in the removal afterwards,
    /// and it carries the same conflicts and failures either way: what happened to each file is true of the
    /// directory's outcome as much as of the directory's own.
    /// </returns>
    /// <remarks>
    /// <b>Windows cannot merge two directories in one move,</b> so this is a walk rather than a call:
    /// everything inside the payload is placed one entry at a time and then the payload, now holding nothing
    /// but empty folders, is removed. The payload goes before the record, for the reason this class is
    /// documented with.
    /// <para>
    /// A file the policy would not place is <em>still lying inside the payload</em>, and removing the
    /// payload afterwards is what would take it with it. Leaving it there and keeping the entry was
    /// considered and rejected: the record would then describe a directory whose payload no longer holds
    /// what the record's size says, and the entry would look restorable for ever while never restoring.
    /// So it goes back into the Recycle Bin as an entry of its own — through
    /// <see cref="NativeRecycleRunner"/>, the machinery <see cref="RecycleBin.RecycleAsync(IReadOnlyList{string?},
    /// RecycleBinOptions?, CancellationToken)"/> uses, rather than through a second implementation of writing
    /// a record — and the caller is handed back a directory whose files are either at their paths or in the
    /// bin again, with the answer saying which is which.
    /// </para>
    /// <para>
    /// Which is also why the payload is removed only once the put-back has emptied it: a file left inside it
    /// is a file the caller has already been told about and then lost, which is the one outcome a Recycle
    /// Bin exists to prevent. An entry the walk could not even describe stops the removal outright for the
    /// same reason.
    /// </para>
    /// </remarks>
    private static RestoreOutcome Merge(
        RecycleBinItem item,
        string targetPath,
        RecycleBinOptions options,
        SemaphoreSlim putBack,
        CancellationToken cancellationToken)
    {
        var state = new MergeState();

        WalkInto(item.PayloadPath, Path.TrimEndingDirectorySeparator(targetPath), options, state, cancellationToken);

        if (state.GaveWay)
        {
            // One record for the directory rather than one per file that gave way: nothing about those files
            // was resolved here — each is an ordinary entry in the next listing, under the path it wanted.
            state.Conflicts.Add(new RecycleRestoreConflict(
                targetPath,
                occupiedByDirectory: true,
                RecycleRestoreConflictAction.MergedSkipped));
        }

        // The put-back runs whether or not anything was left undescribable, because an entry the
        // filesystem would not describe is not one to give up on — everything else in the payload can
        // still be taken out of it. What stops the removal is the code it left behind.
        var refused = PutBack(state.LeftBehind, options, putBack, state, cancellationToken) ?? state.Incomplete;

        if (refused is null)
        {
            try
            {
                Directory.Delete(item.PayloadPath, recursive: true);
                File.Delete(item.IndexPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The files are already back; the item stays in the bin and the failure is reported, which is
                // the same restored-but-still-listed state the move-then-delete order leaves everywhere else.
                refused = ex.HResult;
            }
        }

        if (refused is not null)
        {
            return new RestoreOutcome(RestoreOutcomeKind.Failed, targetPath, item.Size)
            {
                OccupiedByDirectory = true,
                HResult = refused.Value,
                Attempts = 1,
                MergeConflicts = state.Conflicts,
                MergeFailures = state.Failures
            };
        }

        return new RestoreOutcome(RestoreOutcomeKind.Merged, targetPath, item.Size)
        {
            OccupiedByDirectory = true,
            MergeConflicts = state.Conflicts,
            MergeFailures = state.Failures
        };
    }

    /// <summary>
    /// Moves everything inside <paramref name="source"/> out to <paramref name="destination"/>, one entry at
    /// a time, and records what it could not place.
    /// </summary>
    /// <param name="source">A directory inside the payload, which the caller's entry is restoring.</param>
    /// <param name="destination">Where that directory's contents belong, which is always there already.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="state">What the walk has done so far, carried down through the recursion.</param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <remarks>
    /// <b>An entry whose destination is free moves whole,</b> contents and all, which is what makes a
    /// directory that is empty arrive: there is no file in it to put anywhere, so a walk that only ever made
    /// the folders on the way to a file would leave it behind. An entry whose destination is taken by a
    /// directory is merged into it, and everything else is settled by
    /// <see cref="RestorePathPlanner.DecideMerged"/>.
    /// <para>
    /// A directory junction is moved rather than walked into. Walking one would leave the tree and put files
    /// back at paths the caller never named, which is the one thing no value of either policy may do; moving
    /// it renames the link and nothing behind it.
    /// </para>
    /// <para>
    /// One file that cannot be placed is reported and left in the payload for the put-back rather than
    /// ending the walk, because everything else in the tree still has an answer to give and a caller
    /// restoring ninety entries does not lose the eighty-nine because the ninetieth was held.
    /// </para>
    /// </remarks>
    private static void WalkInto(
        string source,
        string destination,
        RecycleBinOptions options,
        MergeState state,
        CancellationToken cancellationToken)
    {
        // Read out before anything is moved, rather than enumerated lazily: this walk moves entries out of
        // the directory it is reading, and a directory the payload is losing is not what to be halfway
        // through discovering.
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wanted = Path.Combine(destination, Path.GetFileName(entry));

            try
            {
                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var occupiedByDirectory = Directory.Exists(wanted);

                if (!occupiedByDirectory && !File.Exists(wanted))
                {
                    MoveEntry(entry, wanted, isDirectory);
                    continue;
                }

                if (isDirectory && (attributes & FileAttributes.ReparsePoint) == 0)
                {
                    // Two directories are a merge. Neither is replaced: the only way to replace one is to
                    // delete what is there first and find out afterwards whether the move was possible at
                    // all, and those bytes are the caller's.
                    WalkInto(entry, wanted, options, state, cancellationToken);
                    continue;
                }

                switch (RestorePathPlanner.DecideMerged(wanted, options.DirectoryConflict))
                {
                    case RestoreAction.Replace:
                        // A file arriving where a directory stands is refused by the move itself rather
                        // than arranged: no merge value replaces a directory, and taking one out would be
                        // deleting the caller's files to make room for the caller's file.
                        MoveEntry(entry, wanted, isDirectory, overwrite: true);
                        state.Conflicts.Add(new RecycleRestoreConflict(
                            wanted,
                            occupiedByDirectory,
                            RecycleRestoreConflictAction.MergedReplaced));
                        break;

                    case RestoreAction.Rename:
                        var free = RestorePathPlanner.WithFreeNumber(wanted);

                        MoveEntry(entry, free, isDirectory);
                        state.Conflicts.Add(new RecycleRestoreConflict(
                            free,
                            occupiedByDirectory,
                            RecycleRestoreConflictAction.MergedRenamed));
                        break;

                    default:
                        state.GaveWay = true;
                        LeaveBehind(entry, wanted, state);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The same rule as the filter on Restore's own switch, for the same reason: the recursive call
                // above is this one, and it raises a cancellation of its own between the children it
                // recurses into. Answered here it would be one file's refusal rather than the caller's own
                // cancellation, and the raise would go no further.
                state.Failures.Add(new RecycleFailure(wanted, ex.HResult, 1, RecycleFailureStage.RestoreFailed));
                LeaveBehind(entry, wanted, state);
            }
        }
    }

    /// <summary>
    /// Sends the entries a merge could not place back into the Recycle Bin, each with a record of its own.
    /// </summary>
    /// <param name="leftBehind">The entries still inside the payload when the walk finished.</param>
    /// <param name="options">The normalised settings the restore runs under, which carry the retry budget.</param>
    /// <param name="putBack">The guard the recycle is written behind.</param>
    /// <param name="state">Collects one failure per entry the recycle would not take.</param>
    /// <param name="cancellationToken">Cancels the put-back between entries.</param>
    /// <returns>
    /// The code the filesystem raised, or <see langword="null"/> when the payload was emptied and can be
    /// removed.
    /// </returns>
    /// <remarks>
    /// Straight to <see cref="NativeRecycleRunner"/> rather than through <see cref="RecycleBatch"/>, and the
    /// difference that matters is what sits in between: the volume gate, which acts on a volume the library
    /// will not recycle on the caller's behalf. That question is already settled here — the volume is holding
    /// the payload this file is being taken out of, and a bin this library could not write to is not one
    /// holding a file it is restoring — so the gate is asked a question whose answer is yes, and under
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> it does not refuse the path but
    /// <em>deletes it</em>, reporting it only in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>
    /// rather than in the failures this method reads. A file destroyed that way leaves
    /// <see cref="RecycleBinResult.Failures"/> empty, so the caller would be told the payload was dealt with
    /// and it would be deleted with the file still inside.
    /// <para>
    /// Under the other two actions the gate is harmless here — a refused path arrives as a failure, and this
    /// method withholds the removal of the payload when it sees one, so nothing is lost either way — which is
    /// exactly why the dangerous case is the quiet one, and why this note is here rather than in a task.
    /// </para>
    /// <para>
    /// One call for the whole list rather than one per file, so that the put-back is the
    /// single writer it is documented to be and this library's own record writer is the only one in the
    /// business.
    /// </para>
    /// <para>
    /// <b>This is where a live cancellation becomes an exception rather than a check between entries.</b> The
    /// token is handed both to the guard and to <see cref="NativeRecycleRunner"/>, and each raises
    /// <see cref="OperationCanceledException"/> from inside this call rather than between two of them. The
    /// caller of this method sits inside a try in <see cref="Restore"/>, so that exception reaches a filter
    /// that must not name it — which is why <see cref="Restore"/>'s filter says so, and why it is written
    /// down here as well as there.
    /// </para>
    /// </remarks>
    private static int? PutBack(
        List<RecycleTarget> leftBehind,
        RecycleBinOptions options,
        SemaphoreSlim putBack,
        MergeState state,
        CancellationToken cancellationToken)
    {
        if (leftBehind.Count == 0)
        {
            return null;
        }

        RecycleBinResult recycled;

        putBack.Wait(cancellationToken);

        try
        {
            recycled = NativeRecycleRunner.Run(leftBehind, options, cancellationToken);
        }
        finally
        {
            putBack.Release();
        }

        if (recycled.Failures.Count == 0)
        {
            return null;
        }

        // One failure per entry the recycle would not take, each reported under the path that entry
        // wanted rather than under where it was lying, which is a path inside the Recycle Bin and the one
        // place the caller cannot do anything with. Keyed rather than scanned, because a tree of leftovers
        // that all failed is the one shape where a scan per failure would be quadratic.
        var wanted = new Dictionary<string, RecycleTarget>(leftBehind.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var target in leftBehind)
        {
            wanted[target.InputPath] = target;
        }

        foreach (var failure in recycled.Failures)
        {
            // Every failure here names a target this list holds, so the second answer is not reached; it
            // stands for the case where it somehow is not, and says the path the recycle itself reported
            // rather than inventing one.
            var named = wanted.TryGetValue(failure.Path, out var target) ? target.RecordPath : failure.Path;

            state.Failures.Add(new RecycleFailure(
                named,
                failure.HResult,
                failure.Attempts,
                RecycleFailureStage.RestoreFailed));
        }

        return recycled.Failures[0].HResult;
    }

    /// <summary>
    /// Keeps one entry of the payload for the put-back, or stops the payload from being removed at all when
    /// the entry cannot even be described well enough to put back.
    /// </summary>
    /// <param name="entry">Where the entry is inside the payload.</param>
    /// <param name="wanted">
    /// Where the entry belongs, which is what its record will name rather than <paramref name="entry"/> — an
    /// entry whose recorded path is inside <c>$RECYCLE.BIN</c> restores into the bin.
    /// </param>
    /// <param name="state">The walk's state, marked incomplete when the entry could not be described.</param>
    private static void LeaveBehind(string entry, string wanted, MergeState state)
    {
        try
        {
            var attributes = File.GetAttributes(entry);

            state.LeftBehind.Add(new RecycleTarget(
                entry,
                Path.GetFullPath(entry),
                attributes,
                (attributes & FileAttributes.Directory) != 0 ? 0 : LengthOf(entry),
                wanted));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing here can be written down, and an entry that cannot be written down cannot be moved out
            // of a payload the caller is about to be told is empty. The payload is left whole, and the
            // refusal is reported under the path the entry wanted rather than lost with it.
            state.Incomplete = ex.HResult;
            state.Failures.Add(new RecycleFailure(wanted, ex.HResult, 1, RecycleFailureStage.RestoreFailed));
        }
    }

    /// <summary>
    /// The length an entry's new record carries, or zero for a directory and for a file whose length the
    /// filesystem would not report.
    /// </summary>
    /// <param name="entry">A file inside the payload.</param>
    /// <remarks>
    /// The answer is zero rather than a raised exception because the alternative is an entry that cannot be
    /// put back at all: the record is written from this number, and a file with no record is a file that
    /// stops being restorable. A record that understates a size costs a listing its byte count; a file that
    /// is destroyed costs the caller the file.
    /// </remarks>
    private static long LengthOf(string entry)
    {
        try
        {
            return new FileInfo(entry).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Puts one entry of the payload down at its destination, as the kind it is.
    /// </summary>
    /// <param name="source">Where the entry is inside the payload.</param>
    /// <param name="destination">Where it is to land, which is either free or has been cleared for it.</param>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="overwrite">
    /// Whether a file is to overwrite what is at <paramref name="destination"/>. Set only under
    /// <see cref="RestoreAction.Replace"/>, which is the caller's own instruction and reaches a file occupant
    /// only because a directory occupant takes the merge instead.
    /// </param>
    /// <remarks>
    /// The two kinds are moved by their own calls because that is all the difference between them, and a
    /// merge is the only place in the library where both are called back to back.
    /// </remarks>
    private static void MoveEntry(string source, string destination, bool isDirectory, bool overwrite = false)
    {
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite);
        }
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
        /// The entry was a directory whose path was already taken by a directory, and its contents were
        /// merged into what was there. What each of its files did is carried beside the slot rather than
        /// being the slot, because one entry can settle any number of them.
        /// </summary>
        /// <remarks>
        /// Counted as one restored entry — one record left the Recycle Bin — whatever the tree held. A file
        /// the merge could not place went back into the bin as an entry of its own, which is one more record
        /// <em>arriving</em> and nothing about this one.
        /// </remarks>
        Merged,

        /// <summary>
        /// The entry stays in the bin and the caller asked for that to be a conflict to read rather than a
        /// failure, so nothing failed.
        /// </summary>
        Skipped,

        /// <summary>
        /// The entry stays in the bin and the caller asked for that to be a failure, so it is reported as
        /// one as well as a conflict — the only way a caller reading the conflicts alone could tell it
        /// from a skip.
        /// </summary>
        Refused,

        /// <summary>
        /// The filesystem refused to put the entry back, or would not say where it came from, and the refusal
        /// is carried with the code it raised.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Refused"/> in that no policy turned an occupied path into this: what
        /// arrives here is a payload deleted since the listing, a destination holding the other kind under a
        /// policy that asked to overwrite it, and a record that cannot be read. The stage is the same either
        /// way — <see cref="RecycleFailureStage.RestoreFailed"/> is where a restore that did not happen is
        /// reported, and <see cref="RecycleFailureStage.RestoreConflictRefused"/> is where a conflict the
        /// caller asked to be told about is.
        /// <para>
        /// A merge that could not finish lands here too, and is the one case where something <em>was</em>
        /// in the way: it settles what it can and then cannot deal with one file, its payload or its record,
        /// and its conflicts travel beside it in <see cref="RestoreOutcome.MergeConflicts"/> rather than
        /// being lost.
        /// </para>
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

        /// <summary>
        /// The conflicts one merged entry settled that are not about the entry itself: what a merge did
        /// about each file of the tree it was restoring. <see langword="null"/> for every entry that was not
        /// a directory merged into one already there, including an entry nothing was decided for at all.
        /// </summary>
        /// <remarks>
        /// Carried beside the slot rather than being one of them, because one entry can settle any number of
        /// files — a tree of ninety collisions is one conflict under
        /// <see cref="RestoreDirectoryConflict.MergeSkip"/> and ninety under the other two, against one
        /// outcome either way — and a slot per file would have to be indexed by something the caller's list
        /// does not have.
        /// </remarks>
        internal IReadOnlyList<RecycleRestoreConflict>? MergeConflicts { get; init; }

        /// <summary>
        /// The failures one merged entry's own files produced, for the same reason: a file of a merged tree
        /// that could not be placed is reported under that file's own path rather than the directory's, and
        /// the directory's outcome says nothing about whether that happened.
        /// </summary>
        internal IReadOnlyList<RecycleFailure>? MergeFailures { get; init; }
    }

    /// <summary>
    /// What one merge has done so far, carried down through the walk and read once the walk is over.
    /// </summary>
    /// <remarks>
    /// Held rather than passed as four parameters because the walk recurses and every level would otherwise
    /// be handing the same four collections on without adding anything.
    /// </remarks>
    private sealed class MergeState
    {
        /// <summary>
        /// What each entry of the tree that found something in the way did about it — and, under
        /// <see cref="RestoreDirectoryConflict.MergeSkip"/>, the one record the directory itself is reported
        /// under rather than one for each of its files.
        /// </summary>
        internal List<RecycleRestoreConflict> Conflicts { get; } = [];

        /// <summary>
        /// The files of the tree the filesystem would not take, each under the path it wanted.
        /// </summary>
        internal List<RecycleFailure> Failures { get; } = [];

        /// <summary>
        /// The entries still inside the payload when the walk finished, which the put-back takes out of it.
        /// </summary>
        internal List<RecycleTarget> LeftBehind { get; } = [];

        /// <summary>
        /// Whether anything was left alone on purpose because the policy would not place it — which is what
        /// <see cref="RestoreDirectoryConflict.MergeSkip"/> answers, and what the directory itself is then
        /// reported under.
        /// </summary>
        internal bool GaveWay { get; set; }

        /// <summary>
        /// Whether something inside the payload could not be accounted for at all: the code the filesystem
        /// raised for an entry that will not describe, or <see langword="null"/> while nothing like that has
        /// happened. It stops the payload from being removed rather than deciding where its contents went.
        /// </summary>
        internal int? Incomplete { get; set; }
    }

    /// <summary>
    /// Reads the slots in the caller's order and reports what they add up to.
    /// </summary>
    /// <remarks>
    /// Each slot goes to the lists its kind names, and to two of them where two is the answer the caller
    /// asked for. The switch is the whole of the mapping rather than something spread through the loop, so
    /// a kind with no arm is reported nowhere instead of in the wrong list.
    /// <para>
    /// What a merge settled goes in before the switch rather than in an arm of it, and reaches the lists
    /// whatever became of the directory itself. A merge can be reported as a failure — because a file of its
    /// tree could not be dealt with, or because its payload would not go — while having overwritten two
    /// files on the way, and a caller reading only the directory's own verdict would be told about neither.
    /// </para>
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
            // Both are set together by the one method that sets either, so this is one condition rather
            // than a second: a slot carrying no conflicts of its own carries no failures either.
            if (slot.MergeConflicts is not null && slot.MergeFailures is not null)
            {
                conflicts.AddRange(slot.MergeConflicts);
                failures.AddRange(slot.MergeFailures);
            }

            switch (slot.Kind)
            {
                case RestoreOutcomeKind.Restored:
                case RestoreOutcomeKind.Replaced:
                case RestoreOutcomeKind.Merged:
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
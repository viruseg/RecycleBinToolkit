using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace RecycleBinToolkit;

/// <summary>Moves entries out of the Recycle Bin and back to the paths they were deleted from.</summary>
/// <remarks>
/// Synchronous: everything it does blocks, and it is only ever given a thread from the pool.
/// <list type="bullet">
/// <item>The payload moves out first and its record is deleted second. The other order leaves a payload that
/// nothing describes when the move fails, which no listing shows and nothing can put back. This order's
/// failure is the opposite shape: the file is already back and its entry is still in the bin.</item>
/// <item>Each entry's outcome goes into a slot taken by its own position in the caller's list, and the lists
/// are flattened in that order once every disk is done. The order work finished in is not an order a caller
/// can rely on.</item>
/// <item>Volumes are grouped by the physical disk behind them and each group runs as a loop, so two volumes on
/// two devices are restored together and two on one device are not. See <see cref="RunDisk"/>.</item>
/// <item>Inside one volume the entries move on that volume's own movers, over a channel that volume owns, so
/// an entry starts moving as it is read rather than after the last one.</item>
/// <item>A cancellation discards the slots. The entries not reached stay in the bin with their records beside
/// their payloads, and a merge that had already set something down keeps it.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class RestoreRunner
{
    /// <summary>Longest wait that can be expressed as a <see cref="TimeSpan"/> from milliseconds.</summary>
    private const double MaxWaitableMilliseconds = (double)int.MaxValue;

    /// <summary>
    /// How far a delay is randomised, as a fraction of itself. Stops a run of entries that are all held by
    /// the same thing from asking again in lockstep with every other restore on the machine.
    /// </summary>
    private const double MaxJitterFraction = 0.2;

    /// <summary>Puts every entry back and reports what happened to each.</summary>
    /// <param name="items">
    /// The entries to restore, as a listing returned them. Their order is the order the answer is reported in.
    /// </param>
    /// <param name="options">
    /// Settings, normalised here rather than trusted: a caller-supplied <see cref="RecycleBinOptions"/> is
    /// not obliged to have been through <see cref="RecycleBinOptions.Normalize"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the operation between entries and between volumes. A cancellation raises rather than returning
    /// a result.
    /// </param>
    /// <returns>What every entry became, with a zero elapsed time the caller overwrites with its own.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A refused move is asked again up to <see cref="RecycleBinOptions.MaxAttempts"/> times, with what
    /// is in the way asked again alongside it. <see cref="RecycleBinOptions.OverallTimeout"/> bounds all of it
    /// and is taken before any disk is read, so a run of refused entries cannot each spend a budget.</item>
    /// <item>One entry's failure never ends the call. Each is that entry's answer in
    /// <see cref="RecycleBinRestoreResult.Failures"/>, carrying the code the filesystem gave where there was
    /// one, and the entries around it keep the outcomes they had.</item>
    /// <item>A volume that named no disk shares one group with every other such volume, so two unknown volumes
    /// move one after another. <see cref="PhysicalDiskKey"/> carries the caveat this rests on.</item>
    /// <item>The token is checked between entries. Which entry a cancellation lands on is not pinned: the
    /// library promises the raise and nothing about the stopping point.</item>
    /// </list>
    /// </remarks>
    /// <param name="sink">
    /// Where each entry's outcome is reported as it is decided, or <see langword="null"/> for a run whose
    /// reports nobody asked for.
    /// </param>
    internal static RecycleBinRestoreResult Run(
        IReadOnlyList<RecycleBinItem> items,
        RecycleBinOptions options,
        ProgressSink? sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        options = options.Normalize();

        // Taken here, before the hop into the pool and before any volume is read, because it is one budget
        // for the whole call: a disk that is slow must not lengthen the budget of one that is not, and one
        // that is fast must not wait for the other to finish with its own.
        var budget = RestoreBudget.Start(options);

        // One slot per entry, in the caller's order. A slot still carrying its opening kind is an entry
        // nothing has been decided for. Two threads never write the same one, because an entry enters the
        // work once and leaves it once.
        var slots = new RestoreOutcome[items.Count];

        var disks = GroupByPhysicalDisk(GroupByVolume(items, cancellationToken));

        // No disk has nothing to do, and one disk has nothing to be parallel with: the caller has already
        // asked for a thread of its own to run this on, and a Parallel.For over a single index would nest
        // another one inside it for no work — the rule the listing applies to a single volume.
        if (disks.Count == 1)
        {
            RunDisk(disks[0], sink, options, budget, slots, cancellationToken);
        }
        else if (disks.Count > 1)
        {
            Parallel.For(
                0,
                disks.Count,
                new ParallelOptions
                {
                    // Each disk is a chain of volume reads and payload moves against one device, so a disk
                    // past the first adds nothing but a wait. The degree that matters is how many there are.
                    MaxDegreeOfParallelism = disks.Count,
                    CancellationToken = cancellationToken
                },
                index => RunDisk(disks[index], sink, options, budget, slots, cancellationToken));
        }

        return Flatten(slots, items, sink);
    }

    /// <summary>
    /// Runs one disk's volumes, and returns before the next one starts.
    /// </summary>
    /// <param name="disk">The volumes sharing one physical disk, in the caller's own order.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="budget">The whole call's retry budget, already running.</param>
    /// <param name="slots">Where each entry's outcome is written, by position in the caller's list.</param>
    /// <param name="cancellationToken">Cancels the operation between entries and between volumes.</param>
    /// <remarks>
    /// <b>This loop is the within-disk exclusion, and it is the whole of it.</b> Each volume's movers are
    /// opened, fed, joined and let go before the next volume's are opened, so two volumes sharing a physical
    /// disk are never moving at the same time — whatever <see cref="MoveDegree"/> answers, and however many
    /// volumes the group holds. Raising the loop's degree would break that; the loop itself cannot be raised.
    /// <list type="bullet">
    /// <item>Nothing a disk's volumes share survives between them: the channel, the movers, the entries
    /// already answered and the put-back guard all belong to the volume <see cref="RunVolume"/> was given.
    /// That makes the separation structural rather than a condition a worker applies to itself, and is why
    /// the disk group carries nothing but volumes.</item>
    /// <item>Two <em>disks</em> are two devices whose <c>$RECYCLE.BIN</c> directories nothing about them
    /// contends for, so they run together.</item>
    /// </list>
    /// </remarks>
    /// <param name="sink">
    /// Where each entry's outcome is reported as it is decided, or <see langword="null"/> for a run whose
    /// reports nobody asked for.
    /// </param>
    private static void RunDisk(
        RestoreDiskGroup disk,
        ProgressSink? sink,
        RecycleBinOptions options,
        RestoreBudget budget,
        RestoreOutcome[] slots,
        CancellationToken cancellationToken)
    {
        foreach (var pipeline in OpenVolumes(disk))
        {
            cancellationToken.ThrowIfCancellationRequested();

            RunVolume(pipeline, sink, options, budget, slots, cancellationToken);
        }
    }

    /// <summary>
    /// Opens the mover pool of every volume on one disk, in the order <see cref="RunDisk"/> runs them.
    /// </summary>
    /// <param name="disk">The volumes sharing one physical disk.</param>
    /// <returns>
    /// One pipeline per volume, each with its own channel, its own movers, its own put-back guard and its own
    /// record of the entries it has answered.
    /// </returns>
    /// <remarks>
    /// The seam between the grouping and the pipeline, and it is where the shape of a restore is decided:
    /// one per <em>volume</em>, so that two volumes of one disk cannot share one pool of movers however many
    /// the figure says there may be. A pool per disk would be one the second volume found already running,
    /// and a pool per call would be one every disk contended for.
    /// </remarks>
    internal static List<RestoreVolumePipeline> OpenVolumes(RestoreDiskGroup disk)
    {
        ArgumentNullException.ThrowIfNull(disk);

        var opened = new List<RestoreVolumePipeline>(disk.Volumes.Count);

        foreach (var volume in disk.Volumes)
        {
            opened.Add(new RestoreVolumePipeline(volume));
        }

        return opened;
    }

    /// <summary>Reads one volume's records and puts back what it read, on the movers this volume owns.</summary>
    /// <param name="pipeline">The volume's own channel, movers, put-back guard and answered set.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="budget">The whole call's retry budget, which this volume shares with every other one.</param>
    /// <param name="slots">Where each entry's outcome is written, by position in the caller's list.</param>
    /// <param name="cancellationToken">Cancels the operation between entries.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The movers are threads of their own rather than pool tasks. A mover blocks on a file move for as
    /// long as the disk takes while the thread feeding this volume is already waiting on it, so a pool task
    /// would be borrowing, from the pool the next disk's mover needs, a thread for work that never yields.</item>
    /// <item>The put-back guard is one per volume rather than one for the call, and so is the set of entries
    /// already answered. Both follow from the scope: two equal entries are in the same volume group, and two
    /// volumes of one disk never run together.</item>
    /// <item><b>Nothing raised here is turned into an entry's answer.</b> A live cancellation reaches this
    /// method from either side, and both are the caller's own cancellation. The first one raised is held back
    /// until every mover has finished and then re-raised as itself.</item>
    /// </list>
    /// </remarks>
    /// <param name="sink">
    /// Where each entry's outcome is reported as it is decided, or <see langword="null"/> for a run whose
    /// reports nobody asked for.
    /// </param>
    private static void RunVolume(
        RestoreVolumePipeline pipeline,
        ProgressSink? sink,
        RecycleBinOptions options,
        RestoreBudget budget,
        RestoreOutcome[] slots,
        CancellationToken cancellationToken)
    {
        // Disposed here rather than by whoever opened the pipeline: this is the only place one is used,
        // and the guard is the only thing in it holding anything. The channel and the answered set are
        // managed and have nothing to release.
        using var putBack = pipeline.PutBack;

        var work = pipeline.Work;

        // What each of them raised, slot zero being this thread's own reading and the rest the movers'. Kept
        // beside the thread that raised it rather than shared, because a thread that lets one out takes the
        // process with it and this one must not; and read only after the join below, which is what makes a
        // write into one slot visible to the reader without a lock of its own.
        var failures = new Exception?[pipeline.Movers + 1];
        var movers = new Thread?[pipeline.Movers];

        try
        {
            for (var i = 0; i < movers.Length; i++)
            {
                var mover = i;

                var thread = new Thread(
                    () =>
                    {
                        try
                        {
                            MoveFrom(pipeline, sink, options, budget, slots, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            failures[mover + 1] = ex;
                        }
                    })
                {
                    // A background thread so that a restore which has already answered cannot hold the
                    // process open; the join below means nothing is left running by the time this call
                    // returns.
                    IsBackground = true,
                    Name = "RecycleBinToolkit restore mover"
                };

                // Assigned only once the thread is running. An unstarted one raises on Join rather than
                // returning, so a slot filled before the start would turn a failure to start into an
                // exception out of the join below — which would lose the failure itself and leave the
                // movers that did start unjoined.
                thread.Start();

                movers[i] = thread;
            }

            FeedVolume(pipeline.Volume, work.Writer, cancellationToken);
        }
        catch (Exception ex)
        {
            failures[0] = ex;
        }
        finally
        {
            // On every way out, and this is the one step that cannot be skipped on any of them. Movers
            // already running are waiting on this channel and nothing else will ever write to it again —
            // including when a thread would not start and half the pool is running, which is the path
            // that would otherwise strand them for the life of the process.
            work.Writer.TryComplete();
        }

        foreach (var mover in movers)
        {
            mover?.Join();
        }

        // The reading's own failure first, then the movers' in order. Rethrown as itself rather than as a new
        // one, so the token is the one the raise carried and the stack is the one it was raised on — an
        // AggregateException would be a different exception from the one this call promises.
        foreach (var failure in failures)
        {
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    /// <summary>How many threads one volume's entries are put back on.</summary>
    /// <param name="volumeRoot">The volume root the figure is asked for.</param>
    /// <returns>How many movers that volume's entries may be moving on at once.</returns>
    /// <remarks>
    /// <b>One.</b> Measured over 300 and 2000 files on a solid-state volume and two mechanical ones, the
    /// curve falls away monotonically to seven or nine times one thread and has the same shape on both kinds
    /// of drive, so the cost is not the seek penalty but contention on the one directory every entry of the
    /// volume shares — the one its records are deleted from. The sweep is in AGENTS.md.
    /// <para>
    /// Raising it widens one axis only: how many movers a volume drains its own channel with. Which volumes
    /// run together is <see cref="RunDisk"/>'s loop rather than a number, and this figure must not become a
    /// way to start movers over several volumes at once — that would put two writers on one head.
    /// </para>
    /// </remarks>
    internal static int MoveDegree(string volumeRoot)
    {
        ArgumentNullException.ThrowIfNull(volumeRoot);

        return 1;
    }

    /// <summary>
    /// Puts back everything one volume's channel carries, until the channel is completed and drained.
    /// </summary>
    /// <param name="pipeline">The volume whose channel this mover drains.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="budget">The whole call's retry budget, which one entry's retrying is spent from.</param>
    /// <param name="slots">Where each entry's outcome is written, by position in the caller's list.</param>
    /// <param name="cancellationToken">Cancels the operation between entries.</param>
    /// <remarks>
    /// <b>One entry at a time, from start to finish, and a merged tree is one entry too.</b> A tree and the
    /// payload holding it are one thing, and two threads on one of them contend for the same directory — the
    /// same reason <see cref="NativeRecycleRunner"/> is single-threaded on the writing side. A directory that
    /// is not a merge is one <see cref="Directory.Move(string, string)"/>, and one that is never reaches
    /// another thread in the middle of its walk.
    /// <para>
    /// The wait is a blocking one on a thread that exists to do this work and nothing else, which is what
    /// <c>WaitToReadAsync</c> is asked for; a caller-visible thread would not be, and this is not one. What
    /// follows it drains everything already written rather than waiting again per entry, so the queue is
    /// entered once per batch rather than once per entry.
    /// </para>
    /// <para>
    /// A cancellation raises out of here and reaches <see cref="RunVolume"/> holding the caller's own token.
    /// It is not caught here: an entry's filesystem refusals are answers and belong in
    /// <see cref="Restore"/>, while this one is the caller stopping the call.
    /// </para>
    /// </remarks>
        /// <param name="sink">Where each entry's outcome is reported as it is decided, or <see langword="null"/> when nobody asked.</param>
    private static void MoveFrom(
        RestoreVolumePipeline pipeline,
        ProgressSink? sink,
        RecycleBinOptions options,
        RestoreBudget budget,
        RestoreOutcome[] slots,
        CancellationToken cancellationToken)
    {
        var reader = pipeline.Work.Reader;

        while (reader.WaitToReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var entry))
            {
                cancellationToken.ThrowIfCancellationRequested();

                Restore(
                    entry.Item,
                    sink,
                    options,
                    budget,
                    slots,
                    pipeline.Settled,
                    pipeline.PutBack,
                    entry.Position,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Puts one volume's entries into its own channel: everything this method will not ask the loader to
    /// read, first, and everything it will after — each written as it lands rather than after the last.
    /// </summary>
    /// <param name="volume">The volume's entries, in the order they were passed in.</param>
    /// <param name="writer">The volume's channel.</param>
    /// <param name="cancellationToken">Cancels the work between records.</param>
    /// <remarks>
    /// <b>Two feeds, and the split between them is the loader's whole contract.</b> An entry a listing
    /// already read has nothing left to read, and an entry whose <c>$I</c> path names no volume is one the
    /// loader's own grouping puts nowhere. Both are written to the channel here, carrying the position they
    /// already have, and <see cref="Restore"/> reads whatever record is there when it reaches them — which
    /// is what happened before this pipeline existed.
    /// <para>
    /// Everything else goes to <see cref="ItemDetailLoader"/>, which skips the entries this method already
    /// took — that skip is what saves a <see cref="Path.GetPathRoot(string)"/> per preloaded entry — and
    /// signals each record as it reads it. <b>Those two skips are why the index the signal answers with is an
    /// index into the list this method handed over</b>, and that list is the entries carrying their own
    /// positions: the signal reads a pair out of it, so the position is never derived from the index. The
    /// coupling is deliberate and one-directional, because the loader is not told about the position at all
    /// and could not use it.
    /// </para>
    /// <para>
    /// Without the callback the load would be a load followed by a restore, which is the shape the caller
    /// already had: every record read before anything moved. What is new is that an entry starts moving while
    /// its neighbours are still being read.
    /// </para>
    /// </remarks>
    private static void FeedVolume(
        RestoreVolumeWork volume,
        ChannelWriter<RestoreWorkItem> writer,
        CancellationToken cancellationToken)
    {
        var count = volume.Items.Count;
        var reading = new List<RestoreWorkItem>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = volume.Items[i];

            if (entry.Item.IsDetailsLoaded || entry.Item.VolumeRoot.IsEmpty)
            {
                writer.TryWrite(entry);
                continue;
            }

            reading.Add(entry);
        }

        if (reading.Count == 0)
        {
            return;
        }

        var feed = new RestoreFeed(writer, reading);

        ItemDetailLoader.Load(new PendingEntries(reading), cancellationToken, in feed);
    }

    /// <summary>
    /// The entries of one volume a load is about to read, in the order it will read them.
    /// </summary>
    /// <param name="entries">
    /// The entries, each carrying the caller's position it will be written back with.
    /// </param>
    /// <remarks>
    /// A view rather than a second list of the same entries: the pair is what carries the position, and a
    /// list of bare entries beside it would put the two back under exactly the assumption this method is
    /// here to remove. The loader indexes rather than enumerates, so the enumerator is there to satisfy the
    /// interface rather than to be called.
    /// </remarks>
    private readonly struct PendingEntries(List<RestoreWorkItem> entries) : IReadOnlyList<RecycleBinItem>
    {
        public int Count => entries.Count;

        public RecycleBinItem this[int index] => entries[index].Item;

        public IEnumerator<RecycleBinItem> GetEnumerator()
        {
            for (var i = 0; i < entries.Count; i++)
            {
                yield return entries[i].Item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Decides what to do about one entry, does it, and records what became of it in the slot its position in
    /// the caller's list gave it — asking again, up to the caller's budget, while the filesystem refuses.
    /// </summary>
    /// <param name="item">The entry, whose record has been read.</param>
    /// <param name="options">The normalised settings the restore runs under.</param>
    /// <param name="budget">The whole call's retry budget, shared by every volume and every disk group.</param>
    /// <param name="slots">Where the outcome is written, by position in the caller's list.</param>
    /// <param name="settled">The entries this volume has already answered.</param>
    /// <param name="putBack">The guard a merge writes its put-back behind.</param>
    /// <param name="position">That entry's position in the caller's list.</param>
    /// <param name="cancellationToken">Cancels the operation between this entry and the next.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>What is in the way is asked about on every attempt rather than assumed, because it is a fact
    /// about the disk rather than about this call. A re-decision may answer something else altogether, and
    /// that answer is acted on like any other.</item>
    /// <item>An entry that is not moved keeps its record and its place in the bin, which is what leaves a
    /// second attempt worth making. Whether that is a conflict or a failure is the answer in the options:
    /// both <c>Error</c> policies answer <see cref="RestoreAction.Skip"/>, and only one also lands in
    /// <see cref="RecycleBinRestoreResult.Failures"/>.</item>
    /// <item>What goes wrong here is an answer rather than an exception. Of two entries named twice one is
    /// moved and the other is reported, and a refusal from the filesystem becomes a
    /// <see cref="RestoreFailureStage.RestoreFailed"/> carrying the code it raised. Only what the filesystem
    /// raises on these paths is caught, so a defect here still surfaces.</item>
    /// <item>The loop is this entry's own. A mover thread holds one entry at a time, so a round shared with
    /// the batch would drive entries already answered, and <see cref="RestoreOutcome.Attempts"/> is the number
    /// of times this payload was put down. The budget is the call's rather than a fresh one per entry.</item>
    /// <item>Nothing here renames, moves or removes what is already on disk except under
    /// <see cref="RestoreAction.Replace"/>, which the caller asked for by name.</item>
    /// </list>
        /// <param name="sink">Where each entry's outcome is reported as it is decided, or <see langword="null"/> when nobody asked.</param>
    /// </remarks>
    private static void Restore(
        RecycleBinItem item,
        ProgressSink? sink,
        RecycleBinOptions options,
        RestoreBudget budget,
        RestoreOutcome[] slots,
        ConcurrentDictionary<RecycleBinItem, byte> settled,
        SemaphoreSlim putBack,
        int position,
        CancellationToken cancellationToken)
    {
        // Hoisted so that a refusal on the way to a decision can still be reported against the path the entry
        // came from. Reading the record and settling the entry are one try because one of them being the
        // thing that raises is not worth the caller losing the other's answer, but only the record read leaves
        // this null, and null is what says there was no path to name.
        string? originalPath = null;

        try
        {
            originalPath = item.OriginalPath;

            // Answered before the destination is looked at, which is the only order in which the answer is the
            // truth: of two identical entries one has already been answered by the time the other is
            // reached, and asking the planner about that would move one payload twice or report one collision
            // twice. Which of the pair moves is the order they were fed in while a volume has one mover and the
            // machine's while it has more — one entry moved and one reported either way. Two equal entries are
            // in the same volume group and a volume group belongs to one volume, so nothing here can be
            // answered by a mover of another volume.
            if (!settled.TryAdd(item, 0))
            {
                Settle(Failed(originalPath, item.Size, hResult: 0, attempts: 0), new RecycleFailure<RestoreFailureStage>(originalPath, 0, 0, RestoreFailureStage.RestoreFailed));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nothing was placed, and the path named is the one the entry came from — which the planner never
            // got to use and the caller needs to see. Only a record that could not be read leaves nothing to
            // name, because that record is the one thing saying where the entry was deleted from, and its own
            // $I path is what is left.
            var failed = Failed(originalPath ?? item.IndexPath, item.Size, ex.HResult, attempts: 0);
            Settle(failed, new RecycleFailure<RestoreFailureStage>(failed.Path, ex.HResult, 0, RestoreFailureStage.RestoreFailed));
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RestoreDecision decision;

            try
            {
                decision = RestorePathPlanner.Decide(originalPath!, item.IsDirectory, options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Where this entry goes could not be asked at all, and asking again is answered by the same
                // record the same way — so this is not retried. What it reports is how many placements were
                // already made before it, which is zero on the first attempt and the entry's own count on any
                // after that.
                var unplaceable = Failed(originalPath!, item.Size, ex.HResult, attempts: attempt - 1);
                Settle(unplaceable, new RecycleFailure<RestoreFailureStage>(originalPath!, ex.HResult, attempt - 1, RestoreFailureStage.RestoreFailed));
                return;
            }

            try
            {
                switch (decision.Action)
                {
                    case RestoreAction.Move when Merges(item, decision, options):
                        Settle(Merge(item, decision.TargetPath, options, putBack, attempt, cancellationToken), failure: null);
                        break;

                    // The three that put a payload down share one arm, because they differ only in the kind
                    // they report and in whether the move is an overwrite — and a retry loop is what they have
                    // in common, which is why it sits outside the switch rather than in three copies of it.
                    case RestoreAction.Move:
                    case RestoreAction.Replace:
                    case RestoreAction.Rename:
                        Settle(PlaceDown(item, ref decision, originalPath!, attempt), failure: null);
                        break;

                    default:
                    {
                        // The kind is the only difference between the two, and reading it rather than
                        // re-deciding it here is what keeps this arm and RestoreRunner.Flatten answering alike.
                        var refused = IsRefusal(item, options);

                        Settle(
                            new RestoreOutcome(
                                refused ? RestoreOutcomeKind.Refused : RestoreOutcomeKind.Skipped,
                                decision.TargetPath,
                                item.Size)
                            {
                                OccupiedByDirectory = decision.OccupiedByDirectory
                            },
                            refused
                                ? new RecycleFailure<RestoreFailureStage>(decision.TargetPath, 0, 0, RestoreFailureStage.RestoreConflictRefused)
                                : null);
                        break;
                    }
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The move is what raised. A merge answers rather than raising, so what reaches here is a
                // refusal to put one payload down — and the only thing to be done with it is to ask the disk
                // again, which is what the next attempt is.
                //
                // This filter must never gain an OperationCanceledException clause. The switch above calls
                // Merge, which reaches PutBack, and that hands the token to a caller which raises it — so a
                // live cancellation raises inside this very try. Reported here it would be this entry's
                // failure rather than the caller's own cancellation, and the raise would go no further.
                if (attempt < options.MaxAttempts && WaitBeforeRetry(options, attempt, budget, cancellationToken))
                {
                    continue;
                }

                // A wait that ended early was the caller's own cancellation rather than a budget that ran out,
                // and this is the only place that can tell the two apart.
                cancellationToken.ThrowIfCancellationRequested();

                slots[position] = Failed(decision.TargetPath, item.Size, ex.HResult, attempt);
                Settle(slots[position], new RecycleFailure<RestoreFailureStage>(decision.TargetPath, ex.HResult, attempt, RestoreFailureStage.RestoreFailed));
                return;
            }
        }

        // Every entry's answer is written once, and the report goes out with it rather than from Flatten
        // afterwards: Flatten runs when the whole call is over, and a call that restores thirty thousand
        // entries would then hand the caller all thirty thousand reports at once.
        void Settle(RestoreOutcome outcome, RecycleFailure<RestoreFailureStage>? failure)
        {
            slots[position] = outcome;
            Report(sink, outcome, failure);
        }
    }

    /// <summary>
    /// Queues one report about a restored entry, or does nothing at all when nobody asked for reports.
    /// </summary>
    /// <remarks>
    /// The status is read off the outcome rather than off the branch that produced it, because
    /// <see cref="Flatten"/> answers the same question once and a second answer here would be free to
    /// drift from it.
    /// </remarks>
    private static void Report(ProgressSink? sink, RestoreOutcome outcome, RecycleFailure<RestoreFailureStage>? failure)
    {
        if (sink is null)
        {
            return;
        }

        var status = outcome.Kind switch
        {
            RestoreOutcomeKind.Merged => RestoreStatus.Merged,
            RestoreOutcomeKind.Skipped => RestoreStatus.Skipped,
            RestoreOutcomeKind.Refused => RestoreStatus.Skipped,
            RestoreOutcomeKind.Failed => RestoreStatus.Failed,
            _ => RestoreStatus.Restored
        };

        sink.Report(new RestoreProgress(sink.NextIndex(), sink.Count, status, outcome.Path, sink.Elapsed, failure));
    }

    /// <summary>
    /// An entry that was not restored, under the path it is to be reported at and the code the filesystem
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
    /// Puts one entry's payload down, takes its record away, and reports which of the two answers that is.
    /// </summary>
    /// <param name="item">The entry whose payload moves and whose record goes.</param>
    /// <param name="decision">
    /// Where the payload is to land and whether the move is an overwrite. Taken by reference because a name
    /// this library chose may be lost to a race, and a failure must name the last name tried rather than the
    /// first — which is the name <see cref="Restore"/> reports. It is not the name the next attempt is made
    /// against: that one is worked out afresh by <see cref="RestorePathPlanner.Decide"/>.
    /// </param>
    /// <param name="wantedPath">The path the entry's own record named, which is what a free name is taken beside.</param>
    /// <param name="attempt">
    /// How many times this payload has been put down so far, which is what a record that will not go is
    /// reported with.
    /// </param>
    /// <remarks>
    /// <b>The second half of the order is here and the retry is not over it.</b> A record whose payload has
    /// already been put back is a failure rather than an exception, because the file is where its caller
    /// wanted it and the entry is left in the bin to be recognised as restored next time — and asking again
    /// would move a payload that is no longer in the bin to put down, which is how the two halves of one entry
    /// stop describing one another.
    /// <para>
    /// The refusal to move does raise rather than answer: it is the one thing here the caller's budget decides,
    /// and <see cref="Restore"/> is what asks again, with the disk asked about what is in the way in between.
    /// </para>
    /// </remarks>
    private static RestoreOutcome PlaceDown(
        RecycleBinItem item,
        ref RestoreDecision decision,
        string wantedPath,
        int attempt)
    {
        Place(item, ref decision, wantedPath);

        try
        {
            // Payload first, record second, for the reason this class is documented with.
            File.Delete(item.IndexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed(decision.TargetPath, item.Size, ex.HResult, attempt);
        }

        return Placed(decision, item.Size);
    }

    /// <summary>
    /// What an entry whose payload is down and whose record has gone becomes.
    /// </summary>
    /// <param name="decision">The decision the payload was put down under.</param>
    /// <param name="size">The bytes the entry carries back, as its own record measured them.</param>
    /// <remarks>
    /// Read off the decision rather than off the arm that reached it, so that a name this library picked under
    /// <see cref="RestoreAction.Rename"/> is the path reported even when a lost race for it moved the payload
    /// to the next free name: what is reported is where the entry <em>is</em>, not which of its four arms
    /// happened to place it.
    /// </remarks>
    private static RestoreOutcome Placed(RestoreDecision decision, long size) =>
        decision.Action switch
        {
            RestoreAction.Replace => new RestoreOutcome(RestoreOutcomeKind.Replaced, decision.TargetPath, size),
            RestoreAction.Rename => new RestoreOutcome(RestoreOutcomeKind.RestoredRenamed, decision.TargetPath, size)
            {
                OccupiedByDirectory = decision.OccupiedByDirectory
            },
            _ => new RestoreOutcome(RestoreOutcomeKind.Restored, decision.TargetPath, size)
        };

    /// <summary>
    /// Puts the payload down at the name <paramref name="decision"/> chooses, leaving the decision to name the
    /// last name it tried.
    /// </summary>
    /// <param name="item">The entry whose payload moves.</param>
    /// <param name="decision">
    /// Where the payload is to land and whether the move is an overwrite, taken by reference so that a name
    /// lost to a race is written back into it.
    /// </param>
    /// <param name="wantedPath">The path the entry's own record named, which is what a free name is taken beside.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A name this library chose is not a failed attempt. Under <see cref="RestoreAction.Rename"/>
    /// somebody else can take the free name between the query and the move, and the next one is taken inside
    /// the same attempt. <see cref="RecycleBinOptions.MaxAttempts"/> is spent on the filesystem refusing the
    /// move, not on somebody else being faster at naming files.</item>
    /// <item>The names are counted against <see cref="NameCollision.NameAttempts"/> so a name taken on every
    /// attempt cannot loop here for ever.</item>
    /// <item>A directory is one <see cref="Directory.Move(string, string)"/> rather than a walk of what is
    /// inside it. A destination something is standing in is not a name that call can land on.</item>
    /// <item>The folder the payload lands in is made here, because the folder
    /// <see cref="RecycleBinItem.OriginalPath"/> sits in need not be there any more. Nothing reaches this
    /// method unless the entry is being put down, so an entry that is not placed leaves no folder tree
    /// behind.</item>
    /// <item>One path is resolved once without its trailing separator, and both the folder taken off it and
    /// the move itself are given that.</item>
    /// </list>
    /// </remarks>
    private static void Place(RecycleBinItem item, ref RestoreDecision decision, string wantedPath)
    {
        // The overwrite is the decision's own rather than a parameter, because it is asked of every attempt
        // and a name taken between two of them does not change what the caller's policy asked for.
        var overwrite = decision.Action == RestoreAction.Replace;

        for (var name = 1; ; name++)
        {
            // The one place a destination is resolved, so the folder made below and the move onto it cannot
            // answer about two different names of the same place.
            var destination = Path.TrimEndingDirectorySeparator(decision.TargetPath);

            CreateTheParentFolder(destination);

            try
            {
                if (item.IsDirectory)
                {
                    Directory.Move(item.PayloadPath, destination);
                }
                else
                {
                    File.Move(item.PayloadPath, destination, overwrite);
                }

                return;
            }
            catch (Exception ex) when (name < NameCollision.NameAttempts && decision.Action == RestoreAction.Rename && NameCollision.IsTaken(ex))
            {
                decision = decision with { TargetPath = RestorePathPlanner.WithFreeNumber(wantedPath) };
            }
        }
    }

    /// <summary>
    /// Waits out the backoff for the attempt that just failed.
    /// </summary>
    /// <param name="options">The normalised settings the delay is derived from.</param>
    /// <param name="attempt">The attempt that just failed, counting from one; the delay doubles on each.</param>
    /// <param name="budget">The whole call's budget, which is what the delay has to fit inside.</param>
    /// <param name="cancellationToken">Cancels the wait, ending the retrying rather than faulting inside it.</param>
    /// <returns>
    /// <see langword="false"/> when no further attempt may start: the backoff does not fit in what is left of
    /// the call's budget.
    /// </returns>
    /// <remarks>
    /// The shape of <see cref="NativeRecycleRunner.WaitBeforeRetry"/> and deliberately not a call to it: that
    /// method is private to a class owning a different retry policy, and the two do not agree about what is
    /// being bounded. That one waits between rounds of one batch, where every round re-drives every path still
    /// pending; this one waits between attempts of one entry, where the count is the entry's own and the budget
    /// is the whole call's. Sharing one would have meant moving a policy that is settled into a class that
    /// does not own it.
    /// </remarks>
    private static bool WaitBeforeRetry(
        RecycleBinOptions options,
        int attempt,
        RestoreBudget budget,
        CancellationToken cancellationToken)
    {
        var exponential = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jittered = exponential * (1 + ((Random.Shared.NextDouble() * 2 - 1) * MaxJitterFraction));

        var delay = TimeSpan.FromMilliseconds(Math.Clamp(jittered, 0, MaxWaitableMilliseconds));

        var remaining = budget.Remaining;

        // One comparison and not the two the copied method carries: a delay is never negative, so a budget
        // that has run out is a delay that does not fit in it, and a second clause for the first case could
        // never be the reason on its own.
        if (delay >= remaining)
        {
            return false;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            Thread.Sleep(delay);
            return true;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            return !cancellationToken.WaitHandle.WaitOne(delay);
        }
        catch (ObjectDisposedException)
        {
            // The caller disposed their token source while this entry was still queued. They have gone, so
            // this entry has nobody left to report to either.
            return false;
        }
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
    /// <param name="attempt">
    /// The attempt this entry was on, which a merge that could not finish is reported at: a merge answers
    /// rather than raising, so this is the same count every other failed placement of this entry would carry.
    /// </param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <returns>
    /// What became of the entry. A merge that could not finish is
    /// <see cref="RestoreOutcomeKind.Failed"/> whether it failed in the walk or in the removal afterwards,
    /// and it carries the same conflicts and failures either way: what happened to each file is true of the
    /// directory's outcome as much as of the directory's own.
    /// </returns>
    /// <remarks>
    /// Windows cannot merge two directories in one move, so this is a walk: everything inside the payload is
    /// placed one entry at a time and the payload, now holding nothing but empty folders, is removed.
    /// <list type="bullet">
    /// <item>A file the policy would not place is still lying inside the payload, so it goes back into the
    /// Recycle Bin as an entry of its own, through <see cref="NativeRecycleRunner"/> rather than a second
    /// implementation of writing a record. The caller is handed back a directory whose files are either at
    /// their paths or in the bin again, and the answer says which.</item>
    /// <item>The payload is removed only once the put-back has emptied it, and an entry the walk could not
    /// describe stops the removal outright. A file left inside it is a file the caller was told about and then
    /// lost.</item>
    /// <item>The payload goes before the record, for the reason this class is documented with.</item>
    /// </list>
    /// </remarks>
    private static RestoreOutcome Merge(
        RecycleBinItem item,
        string targetPath,
        RecycleBinOptions options,
        SemaphoreSlim putBack,
        int attempt,
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
                Attempts = attempt,
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
                state.Failures.Add(new RecycleFailure<RestoreFailureStage>(wanted, ex.HResult, 1, RestoreFailureStage.RestoreFailed));
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
    /// <list type="bullet">
    /// <item>Straight to <see cref="NativeRecycleRunner"/> rather than through <see cref="RecycleBatch"/>. The
    /// volume gate sits in between and acts on a volume the library will not recycle on the caller's behalf:
    /// under <see cref="NoRecycleBinAction.DeletePermanently"/> it would not refuse the path but delete it,
    /// reporting that only in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>. That leaves
    /// <see cref="RecycleBinResult.Failures"/> empty, so the caller would be told the payload was dealt with
    /// and it would be deleted with a file still inside.</item>
    /// <item>One call for the whole list rather than one per file, so the put-back is the single writer it is
    /// documented to be.</item>
    /// <item>This is where a live cancellation becomes an exception rather than a check between entries: the
    /// token goes both to the guard and to <see cref="NativeRecycleRunner"/>, and each raises
    /// <see cref="OperationCanceledException"/> from inside this call.</item>
    /// </list>
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
            // No sink: these are files of the caller's that the merge could not put back, and they are lying inside
            // the Recycle Bin rather than anywhere in the caller's list. Reporting them would push the
            // reports' own index past the count the caller is holding, for items they never asked about.
            recycled = NativeRecycleRunner.Run(leftBehind, sink: null, options, cancellationToken);
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

            state.Failures.Add(new RecycleFailure<RestoreFailureStage>(
                named,
                failure.HResult,
                failure.Attempts,
                RestoreFailureStage.RestoreFailed));
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
            state.Failures.Add(new RecycleFailure<RestoreFailureStage>(wanted, ex.HResult, 1, RestoreFailureStage.RestoreFailed));
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
    /// One work item per entry, grouped into one list per volume, in the order the volumes first appear
    /// among the entries and each list in the caller's own order.
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Keyed without regard to case, as the root itself is: Windows writes <c>X:\</c> and <c>x:\</c> for
    /// the same volume, and two spellings would read one disk twice under two policies.</item>
    /// <item>A position is taken here, once, rather than resolved later by asking an entry which index it is.
    /// It is what keeps the answer in the caller's order whatever order the work finished in.</item>
    /// <item>One volume holds every copy of one entry, because an entry's volume is a prefix of its own record
    /// path. That is what lets the entries already answered be a per-volume set rather than a per-call
    /// one.</item>
    /// <item>Nothing removes a key, so the volumes come out in the order the entries first named them.</item>
    /// </list>
    /// </remarks>
    private static List<RestoreVolumeWork> GroupByVolume(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var volumes = new Dictionary<string, List<RestoreWorkItem>>(StringComparer.OrdinalIgnoreCase);
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

            group.Add(new RestoreWorkItem(items[position], position));
        }

        return [.. volumes.Select(volume => new RestoreVolumeWork(volume.Key, volume.Value))];
    }

    /// <summary>
    /// Gathers the volumes by the physical disk they are partitions of, in the terms of a restore.
    /// </summary>
    /// <param name="byVolume">The volumes, in the order the caller's entries named them.</param>
    /// <returns>
    /// One group per disk the volumes named, in the order those disks were first named, and — when any
    /// volume named none — one further group holding every such volume.
    /// </returns>
    /// <remarks>
    /// The rule itself is <see cref="VolumeDiskGroups"/>, which an empty shares: two operations deciding
    /// which volumes contend for one head are the same question, and two copies of it are free to drift.
    /// What this adds is only the language — a group of <see cref="RestoreVolumeWork"/> rather than of
    /// positions into a list of roots the caller never had.
    /// </remarks>
    internal static List<RestoreDiskGroup> GroupByPhysicalDisk(IReadOnlyList<RestoreVolumeWork> byVolume)
    {
        ArgumentNullException.ThrowIfNull(byVolume);

        var roots = new string[byVolume.Count];

        for (var position = 0; position < byVolume.Count; position++)
        {
            roots[position] = byVolume[position].VolumeRoot;
        }

        return
        [
            .. VolumeDiskGroups.Group(roots).Select(
                group => new RestoreDiskGroup(group.Disk, [.. group.Positions.Select(position => byVolume[position])]))
        ];
    }

    /// <summary>
    /// Puts every entry that is ready to be moved into one volume's channel.
    /// </summary>
    /// <param name="writer">The volume's channel.</param>
    /// <param name="reading">
    /// The entries handed to the loader, each still carrying the caller's position it will be written back
    /// with. The signal's index is an index into this list, so the pair comes out of it whole.
    /// </param>
    /// <remarks>
    /// A struct reached through references to what it holds, so <see cref="ItemDetailLoader"/> may copy it
    /// into its own local for the read loop and every copy still names the same channel and the same
    /// entries. The code the read reported is not carried on the work item: <see cref="Restore"/> asks the
    /// entry for its path and is told by the exception, which is the failure it already reports.
    /// </remarks>
    private readonly struct RestoreFeed(ChannelWriter<RestoreWorkItem> writer, List<RestoreWorkItem> reading) : IRestoreSignal
    {
        /// <inheritdoc />
        public void OnReady(int index, RecycleBinItem item, int hResult) => writer.TryWrite(reading[index]);
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
        /// way — <see cref="RestoreFailureStage.RestoreFailed"/> is where a restore that did not happen is
        /// reported, and <see cref="RestoreFailureStage.RestoreConflictRefused"/> is where a conflict the
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
        /// Carried through exactly as the exception carried it, because <see cref="RecycleFailure{TStage}.HResult"/>
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
        internal IReadOnlyList<RecycleFailure<RestoreFailureStage>>? MergeFailures { get; init; }
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
        internal List<RecycleFailure<RestoreFailureStage>> Failures { get; } = [];

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
    /// <param name="slots">One slot per entry, by position in the caller's list.</param>
    /// <param name="items">
    /// The caller's entries, which is where an undecided slot's entry is named from — the one case the slot
    /// itself has nothing to report.
    /// </param>
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
    /// <para>
    /// <b><see cref="RestoreOutcomeKind.Undecided"/> is an arm rather than an omission.</b> Every path
    /// through the pipeline writes an answer for every entry it was handed, so a slot still carrying its
    /// opening kind means the work never reached that entry — and the first thing a caller would be told is
    /// that their entry was restored, conflicted or failed, when it is in none of the three. It is reported
    /// as a failure under the entry's own record, which is the only path an entry carries without anything
    /// having read it, and which is what a record that could not be read is named under as well.
    /// </para>
    /// <para>
    /// This arm exists for the paths that might not write. Producing one needs a defect in the pipeline —
    /// an entry written to a channel that drops it — and the way to arrange that would be a hook inside
    /// the engine rather than a thing a run can arrange from outside.
    /// </para>
    /// </remarks>
    /// <param name="sink">
    /// Where an entry nothing decided is reported. Every other outcome has already gone out as it was
    /// reached, which is why only this one needs it.
    /// </param>
    private static RecycleBinRestoreResult Flatten(
        RestoreOutcome[] slots,
        IReadOnlyList<RecycleBinItem> items,
        ProgressSink? sink)
    {
        var restoredPaths = new List<string>(slots.Length);
        var conflicts = new List<RecycleRestoreConflict>();
        var failures = new List<RecycleFailure<RestoreFailureStage>>();
        long itemsRestored = 0;
        long restoredSize = 0;

        for (var position = 0; position < slots.Length; position++)
        {
            var slot = slots[position];

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
                    failures.Add(new RecycleFailure<RestoreFailureStage>(slot.Path, 0, 0, RestoreFailureStage.RestoreConflictRefused));
                    break;

                case RestoreOutcomeKind.Failed:
                    failures.Add(new RecycleFailure<RestoreFailureStage>(slot.Path, slot.HResult, slot.Attempts, RestoreFailureStage.RestoreFailed));
                    break;

                case RestoreOutcomeKind.Undecided:
                    // The one outcome nothing ever writes, so there is no write to hang the report on. It is
                    // reported here, where the entry's own record is known — the only place it is — and it
                    // lands last because nothing else is left by then.
                    var failure = new RecycleFailure<RestoreFailureStage>(
                        items[position].IndexPath,
                        0,
                        0,
                        RestoreFailureStage.RestoreFailed);

                    failures.Add(failure);
                    Report(
                        sink,
                        new RestoreOutcome(RestoreOutcomeKind.Failed, items[position].IndexPath, 0),
                        failure);
                    break;
            }
        }

        return new RecycleBinRestoreResult(restoredPaths, conflicts, failures, itemsRestored, restoredSize, TimeSpan.Zero);
    }
}

/// <summary>One entry of a restore, together with where its answer is to be written.</summary>
/// <param name="Item">The entry, carrying whatever its record resolved to.</param>
/// <param name="Position">
/// Where the entry sat in the caller's list, which is the slot its outcome goes into. Taken once while the
/// entries are being grouped by volume and carried from there, because that is the only place the caller's
/// ordering and the entry's identity are in the same hands.
/// </param>
internal readonly record struct RestoreWorkItem(RecycleBinItem Item, int Position);

/// <summary>
/// The retry budget of one whole call: a clock and the deadline it was measured against.
/// </summary>
/// <param name="clock">The call's own clock, which <paramref name="deadline"/> was taken from.</param>
/// <param name="deadline">
/// When the call's <see cref="RecycleBinOptions.OverallTimeout"/> runs out, whatever the call is doing then.
/// </param>
/// <remarks>
/// <b>One budget, taken before the call reaches the pool, and carried down to every mover of every volume.</b>
/// A deadline derived where it is waited on would be a fresh budget for each entry that has to wait, and a
/// batch of refused entries would then take a budget apiece — which is the case a caller who set a budget at
/// all is exactly the one that hits. A slow disk does not lengthen a fast one's budget either, because the
/// deadline is not derived from anything a disk does.
/// <para>
/// It is a value rather than a field on the pipeline because the budget is the call's and not a volume's:
/// <see cref="RestoreVolumePipeline"/> is opened once per volume, and a call with one volume and a call with
/// four must spend the same budget.
/// </para>
/// </remarks>
internal readonly struct RestoreBudget(Stopwatch clock, TimeSpan deadline)
{
    /// <summary>How much of the budget is left, or nothing at all once it has run out.</summary>
    internal TimeSpan Remaining => deadline - clock.Elapsed;

    /// <summary>Takes the budget for a call that is about to start.</summary>
    /// <param name="options">The normalised settings the budget is taken from.</param>
    internal static RestoreBudget Start(RecycleBinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var started = Stopwatch.StartNew();

        return new RestoreBudget(started, started.Elapsed + options.OverallTimeout);
    }
}

/// <summary>One volume's entries, and the root it is being restored from.</summary>
/// <param name="VolumeRoot">The volume root these entries' records live on.</param>
/// <param name="Items">
/// The entries of that volume, in the order they appeared in the caller's list. Each carries its own
/// position, so an outcome is written to the caller's slot rather than to one taken from the order the work
/// finished in.
/// </param>
internal sealed record RestoreVolumeWork(string VolumeRoot, List<RestoreWorkItem> Items);

/// <summary>Every volume that named one physical disk, which is the unit the pipeline is split on.</summary>
/// <param name="Disk">
/// The disk the volumes are partitions of, or <see langword="null"/> for the group of volumes that named no
/// disk at all — a share, or a volume whose driver declined, which is a volume that could not say where its
/// bytes are and is therefore grouped with every other such volume.
/// </param>
/// <param name="Volumes">The volumes on that disk, in the order the caller's entries named them.</param>
/// <remarks>
/// The group decides what runs together and nothing else: it carries volumes, and
/// <see cref="RestoreRunner.OpenVolumes"/> gives each of them its own
/// <see cref="RestoreVolumePipeline"/>. Two volumes on one disk are therefore never moving at the same time,
/// which is a property of <see cref="RestoreRunner"/>'s loop over this and not of any figure.
/// <para>
/// The caveat in <see cref="PhysicalDiskKey"/> is the limit of what the key can promise: a spanning or
/// striped volume is named after the storage it is a volume of, so two volumes can be here together while
/// sharing more than one head.
/// </para>
/// </remarks>
internal sealed record RestoreDiskGroup(PhysicalDiskKey? Disk, List<RestoreVolumeWork> Volumes);

/// <summary>
/// One volume's work in flight: the channel its records are read into and the movers that drain it.
/// </summary>
/// <remarks>
/// <b>One per volume, and that is the whole of the shape.</b> A volume's entries may be moved by
/// <see cref="RestoreRunner.MoveDegree"/> threads at once; two volumes of one disk may not be moving at
/// all at once, and a pipeline per volume is what makes the first true without the second being a
/// condition anything checks.
/// <para>
/// It carries everything the volume's movers share — the channel, the answered set and the put-back guard —
/// so that nothing about one volume's work is reachable from another. A call has one of these per volume,
/// a disk group has one per volume through <see cref="RestoreRunner.OpenVolumes"/>, and two of them have
/// never been the same object.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal sealed class RestoreVolumePipeline
{
    /// <summary>Opens the movers, the channel and the state they share for one volume.</summary>
    /// <param name="volume">The volume whose entries this pipeline restores.</param>
    internal RestoreVolumePipeline(RestoreVolumeWork volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        Volume = volume;
        Movers = RestoreRunner.MoveDegree(volume.VolumeRoot);

        // Neither of these is true. A volume's records are read by a Parallel.For body, so there is more
        // than one thread writing here; and there is one reader per mover and more than one mover once
        // the degree is above one. Saying otherwise would let the channel take locks to protect a promise
        // it is not being given.
        //
        // AllowSynchronousContinuations is left at its default, which is to let a reader be woken on the
        // thread that woke it. What runs here is the completion of one wait, not the move — the mover is on
        // a thread of its own and blocked on that wait — so nothing is displaced. Stated because setting it
        // false would move the waiting to the pool, and a mover that runs on somebody else's pool thread is
        // the shape the paragraph above is about.
        Work = Channel.CreateUnbounded<RestoreWorkItem>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = true
        });

        Settled = new ConcurrentDictionary<RecycleBinItem, byte>();
        PutBack = new SemaphoreSlim(1, 1);
    }

    /// <summary>The volume whose entries this pipeline restores.</summary>
    internal RestoreVolumeWork Volume { get; }

    /// <summary>
    /// How many movers this volume's entries may be moving on at once, which is
    /// <see cref="RestoreRunner.MoveDegree"/> asked of this volume's own root.
    /// </summary>
    internal int Movers { get; }

    /// <summary>The channel its records are read into and its movers drain.</summary>
    internal Channel<RestoreWorkItem> Work { get; }

    /// <summary>
    /// The entries this pipeline has answered, so that a list naming one twice moves it once. Keyed by the
    /// entry's own identity — its <c>$I</c> path — which is one string comparison and reads no record. It
    /// belongs to the volume rather than the call because two equal entries are in the same volume group,
    /// so a volume's own set answers each of them once.
    /// </summary>
    internal ConcurrentDictionary<RecycleBinItem, byte> Settled { get; }

    /// <summary>
    /// The guard a merge writes its put-back behind: one writer into this volume's <c>$RECYCLE.BIN</c> at a
    /// time, whatever <see cref="RestoreRunner.MoveDegree"/> answers.
    /// </summary>
    internal SemaphoreSlim PutBack { get; }
}

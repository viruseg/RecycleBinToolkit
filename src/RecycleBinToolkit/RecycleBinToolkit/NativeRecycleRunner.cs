using System.Diagnostics;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves paths into <c>$RECYCLE.BIN</c> by writing the record and moving the payload.
/// </summary>
/// <remarks>
/// Synchronous, because everything it does blocks: a file move is a metadata operation the thread waits
/// on. It is only ever given a thread from the pool, never a caller's.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class NativeRecycleRunner
{
    /// <summary>Longest wait that can be expressed as a <see cref="TimeSpan"/> from milliseconds.</summary>
    private const double MaxWaitableMilliseconds = (double)int.MaxValue;

    /// <summary>Win32 <c>ERROR_FILENAME_EXCED_RANGE</c>: the name is longer than the system will take.</summary>
    private const int PathTooLong = unchecked((int)0x800700CE);

    /// <summary>
    /// How far a delay is randomised, as a fraction of itself. Stops a batch of locked files from retrying
    /// in lockstep with every other batch on the machine.
    /// </summary>
    private const double MaxJitterFraction = 0.2;

    /// <summary>
    /// Moves a batch to the Recycle Bin and reports what happened to each path.
    /// </summary>
    /// <param name="targets">Validated paths, already cleared of volumes that cannot recycle them.</param>
    /// <param name="options">
    /// Retry and verification settings. Normalised here rather than trusted, because a caller-supplied
    /// <see cref="RecycleBinOptions"/> is not obliged to have been through
    /// <see cref="RecycleBinOptions.Normalize"/> and an unclamped <see cref="RecycleBinOptions.MaxAttempts"/>
    /// of zero would otherwise run no rounds at all and report a batch that recycled nothing and failed
    /// nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation between paths.</param>
    /// <returns>The outcome of the batch.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>One thread, deliberately.</b> Every record written and every payload moved here goes into one
    /// directory, and a directory is the one thing a filesystem cannot take concurrent writers on. Measured
    /// over a three-thousand file batch, eight threads finish two and a half times slower than one. The sweep
    /// is in AGENTS.md.</item>
    /// <item>Retries are per path rather than per batch: a move that fails is a property of one path, and
    /// retrying the others would repeat work that already succeeded.</item>
    /// </list>
    /// </remarks>
    internal static RecycleBinResult Run(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        options = options.Normalize();

        var stopwatch = Stopwatch.StartNew();

        if (targets.Count == 0)
        {
            return RecycleBinResult.Create([], [], [], stopwatch.Elapsed);
        }

        var collapsed = RecycleTarget.CollapseEquivalent(targets);
        var pending = collapsed;
        var recycled = new List<string>(collapsed.Count);
        var failuresByPath = new Dictionary<string, RecycleFailure>(collapsed.Count, StringComparer.OrdinalIgnoreCase);
        var permanentlyDeleted = new List<string>();
        var staged = new List<StagedTarget>();
        var stagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var stager = new LongPathStager();

        // A record with a fixed path field cannot name a path past its length, and no retry changes that.
        // Settled once, before the rounds, because it is a property of the record and not of any path.
        pending = SettleOverLongPaths(
            collapsed,
            options,
            IndexFormat.VersionForWriting,
            stager,
            staged,
            stagedPaths,
            permanentlyDeleted,
            failuresByPath);

        // One bin directory per volume, created at most once for the whole operation, and one sequencer of
        // names beside it for the same reason: twenty thousand paths on one volume need the directory made
        // once and the names handed out from one counter, and twenty thousand of either is twenty thousand
        // round trips the caller should not be paying.
        var bins = new Dictionary<string, (string Path, BinStemSequencer Stems)>(StringComparer.OrdinalIgnoreCase);

        // Taken once, before the loop, because the budget it bounds is the batch's and not each round's.
        // Added here rather than inside the wait it is checked by: a deadline recomputed there is a fresh
        // budget every round, and the loop then runs to MaxAttempts whenever one delay fits inside it — which
        // is the case a caller who set a budget at all is the one that hits.
        var deadline = stopwatch.Elapsed + options.OverallTimeout;

        IReadOnlyList<string> leftovers;

        try
        {
            for (var round = 1; round <= options.MaxAttempts && pending.Count > 0; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stillPending = new List<RecycleTarget>(pending.Count);

                foreach (var target in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    RecycleFailure? failure = null;
                    var moved = false;

                    // The seam is asked before the move, not after: a fault that let the payload move and
                    // then threw the answer away would leave the item in the bin, which is the opposite of
                    // what a case provoking it needs to see.
                    if (!FaultAfterStaging || !stagedPaths.Contains(target.InputPath))
                    {
                        moved = TryRecycle(target, bins, cancellationToken, out failure);
                    }

                    if (moved)
                    {
                        recycled.Add(target.InputPath);
                        continue;
                    }

                    stillPending.Add(target);

                    if (failure is not null)
                    {
                        failuresByPath[failure.Path] = new RecycleFailure(failure.Path, failure.HResult, round, failure.Stage);
                    }
                }

                pending = stillPending;

                if (pending.Count == 0 || round == options.MaxAttempts)
                {
                    break;
                }

                if (!WaitBeforeRetry(options, round, stopwatch, deadline, cancellationToken))
                {
                    break;
                }
            }
        }
        finally
        {
            PutBackUnrecycled(staged, recycled);
            leftovers = stager.Release();
        }

        // The whole batch went, which is the outcome nearly every batch has. Nothing is left to report and
        // nothing is left to have given up, so the two collections below would both come out empty — and
        // assembling them first means hashing every InputPath in the batch twice for an answer already
        // decided. Each of those paths is long and compared case-insensitively, which is why it shows up.
        if (failuresByPath.Count == 0 && pending.Count == 0)
        {
            return RecycleBinResult.Create(recycled, permanentlyDeleted, [], stopwatch.Elapsed, leftovers);
        }

        // A path that gave up with no verdict of its own is one the loop stopped before it could record, and
        // the round it stopped on is the one it is reported at. Held as a set rather than found by scanning
        // the pending list: the scan compares RecycleTarget structs field by field, and asking N of them
        // about a list of N is N squared of that on a batch where every path failed — which is the shape the
        // branch below exists for.
        var unfinished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in pending)
        {
            unfinished.Add(target.InputPath);
        }

        // The batch as it was collapsed, not collapsed again: the loop replaced the pending list with the
        // subset it left behind, and hashing every long path through OrdinalIgnoreCase a second time to
        // arrive at the identical list is a pass this does not need to make.
        var reported = new List<RecycleFailure>(failuresByPath.Count);

        foreach (var target in collapsed)
        {
            if (failuresByPath.TryGetValue(target.InputPath, out var failure))
            {
                reported.Add(failure);
            }
            else if (unfinished.Contains(target.InputPath))
            {
                reported.Add(new RecycleFailure(target.InputPath, 0, options.MaxAttempts, RecycleFailureStage.Performing));
            }
        }

        return RecycleBinResult.Create(recycled, permanentlyDeleted, reported, stopwatch.Elapsed, leftovers);
    }

    /// <summary>
    /// Takes out of the batch every path the record in use cannot name, and applies the caller's policy to
    /// it.
    /// </summary>
    /// <remarks>
    /// Done before the rounds rather than inside them, because no attempt changes the outcome: the record's
    /// path field is a fixed number of bytes and the path is the path. Retrying a path that cannot be
    /// recorded would spend the caller's whole backoff to arrive at the same refusal six times.
    /// <para>
    /// Version 2 has a counted path and is left entirely alone. Every path a version 2 machine can move is
    /// a path its record can name, so running this over it would cost a length comparison per item and
    /// change nothing.
    /// </para>
    /// </remarks>
    private static List<RecycleTarget> SettleOverLongPaths(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        int version,
        LongPathStager stager,
        List<StagedTarget> staged,
        HashSet<string> stagedPaths,
        List<string> permanentlyDeleted,
        Dictionary<string, RecycleFailure> failuresByPath)
    {
        if (version != IndexFormat.Legacy)
        {
            return [.. targets];
        }

        var pending = new List<RecycleTarget>(targets.Count);

        foreach (var target in targets)
        {
            // RecordPath rather than FullPath: the prefix is a .NET convenience the record never carries,
            // so it is the recorded spelling whose length decides this.
            if (target.RecordPath.Length <= NativeIndexFile.LegacyPathCapacity)
            {
                pending.Add(target);
                continue;
            }

            if (options.LongPathFailureAction == LongPathFailureAction.DeletePermanently)
            {
                if (PermanentDeleter.TryDelete(target, permanentlyDeleted, out var refused))
                {
                    continue;
                }

                // The file system gave a reason of its own, so report that rather than blaming the length.
                if (refused != 0)
                {
                    failuresByPath[target.InputPath] = new RecycleFailure(
                        target.InputPath,
                        refused,
                        0,
                        RecycleFailureStage.PermanentDeleteFailed);

                    continue;
                }
            }

            // Tried after the delete the caller asked for and before the refusal they did not, because the
            // alias is free: the file does not move, and the record names a path the caller can restore from.
            // The caller never learns it happened unless they look, which is what makes it the right first
            // answer — it is also the only one that changes nothing.
            if (TryTakeAlias(target, out var aliased))
            {
                pending.Add(aliased);
                continue;
            }

            // The last answer, and the only one that moves anything. Both spellings follow the item: the
            // record names the staging path because the original is what would not fit, and a restore
            // puts the item back there.
            if (options.LongPathFailureAction == LongPathFailureAction.ShortenPathAndRecycle
                && TryStageWithBatchRetries(target, stager, staged, stagedPaths, out var stagedTarget))
            {
                pending.Add(stagedTarget);
                continue;
            }

            failuresByPath[target.InputPath] = new RecycleFailure(
                target.InputPath,
                PathTooLong,
                0,
                RecycleFailureStage.LongPathNotRecyclable);
        }

        return pending;
    }

    /// <summary>
    /// Stages one path, and returns whether it got there.
    /// </summary>
    /// <remarks>
    /// One attempt per call. The caller gives a whole batch one attempt each before any of them waits, so
    /// that a contended batch pays the backoff once rather than once per item.
    /// </remarks>
    private static bool TryStageWithBatchRetries(
        RecycleTarget target,
        LongPathStager stager,
        List<StagedTarget> staged,
        HashSet<string> stagedPaths,
        out RecycleTarget stagedTarget)
    {
        var stagedPath = stager.TryStageOnce(target.FullPath);

        if (stagedPath is null)
        {
            stagedTarget = default;
            return false;
        }

        stagedTarget = target.WithStagedPath(stagedPath);
        staged.Add(new StagedTarget(stagedTarget, target.FullPath));
        stagedPaths.Add(target.InputPath);
        return true;
    }

    /// <summary>
    /// Moves back every staged item whose recycle did not go through.
    /// </summary>
    /// <remarks>
    /// Only items that are not in <paramref name="recycledPaths"/>. An item already in the Recycle Bin has
    /// nothing left at its staging path to move, and trying anyway would fail against a path this library
    /// has just removed.
    /// </remarks>
    private static void PutBackUnrecycled(List<StagedTarget> staged, List<string> recycledPaths)
    {
        if (staged.Count == 0 || FaultBeforePutBack)
        {
            return;
        }

        var recycled = new HashSet<string>(recycledPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in staged)
        {
            if (!recycled.Contains(entry.Target.InputPath))
            {
                LongPathStager.TryRestore(entry.Target.FullPath, entry.OriginalFullPath);
            }
        }
    }

    /// <summary>An item moved to a short path, with where it came from so it can be put back.</summary>
    private readonly record struct StagedTarget(RecycleTarget Target, string OriginalFullPath);

    /// <summary>
    /// Whether a staged item's recycle is refused even though nothing is wrong with it.
    /// </summary>
    /// <remarks>
    /// A seam for the suite, which has to witness what happens to an item that could not be recycled and
    /// could not be moved back. Off in production; there is no other way to reach the branch, because the
    /// pre-cancelled token the suite used before is refused before this method is entered.
    /// </remarks>
    internal static bool FaultAfterStaging { get; set; }

    /// <summary>Whether the put-back of a staged item is skipped, so the staging directory keeps what it holds.</summary>
    internal static bool FaultBeforePutBack { get; set; }

    /// <summary>
    /// Replaces a target's recorded path with the volume's short name for it, when it has one that fits.
    /// </summary>
    /// <remarks>
    /// Both spellings have to fit, and only the recorded one is measured: a short name that is itself too
    /// long is not a shorter path, and writing it would produce a record holding a path nothing can name.
    /// </remarks>
    private static bool TryTakeAlias(RecycleTarget target, out RecycleTarget aliased)
    {
        aliased = default;

        var alias = ShortPathResolver.TryResolveShort(target.RecordPath);

        if (alias is null || alias.Length > NativeIndexFile.LegacyPathCapacity)
        {
            return false;
        }

        aliased = target.WithRecordPath(alias);
        return true;
    }

    /// <summary>
    /// Writes the record, moves the payload, and takes the record away again if the move did not happen.
    /// </summary>
    /// <remarks>
    /// The order is the whole design. The record goes first, so a crash between the two leaves the caller's
    /// file exactly where it was and an index with no payload — a phantom the next empty collects. The other
    /// order leaves data that is gone and recorded nowhere, which is the one outcome a Recycle Bin exists to
    /// prevent.
    /// </remarks>
    private static bool TryRecycle(RecycleTarget target, Dictionary<string, (string Path, BinStemSequencer Stems)> bins, CancellationToken cancellationToken, out RecycleFailure? failure)
    {
        var root = Path.GetPathRoot(target.FullPath);

        if (string.IsNullOrEmpty(root))
        {
            failure = new RecycleFailure(target.InputPath, 0, 0, RecycleFailureStage.PathValidation);
            return false;
        }

        if (!bins.TryGetValue(root, out var bin))
        {
            var binDirectory = NativeBinLayout.EnsureBinDirectory(root);
            bin = (binDirectory, new BinStemSequencer(binDirectory, cancellationToken));
            bins.Add(root, bin);
        }

        // Asked once for the whole operation rather than per item, because it is a property of the
        // machine and of nothing in the batch.
        var version = IndexFormat.VersionForWriting;

        var isDirectory = target.IsDirectory;
        var extension = Path.GetExtension(target.FullPath);

        Span<char> stem = stackalloc char[NativeBinLayout.StemLength];

        for (var attempt = 1; ; attempt++)
        {
            bin.Stems.Next(stem);

            var indexPath = NativeBinLayout.IndexPath(bin.Path, stem, extension);
            var payloadPath = NativeBinLayout.PayloadPath(bin.Path, stem, extension);

            try
            {
                // No query at all. Validation already read the size, and it read it from the same query
                // that told it the path is a file — which is why the record costs nothing to produce beyond
                // the write itself.
                NativeIndexFile.Write(indexPath, target.RecordPath, target.Size, DateTime.UtcNow, version);
            }
            catch (IOException ex) when (attempt < NameCollision.NameAttempts && NameCollision.IsTaken(ex))
            {
                // The record's name was taken. CreateNew refused rather than overwriting somebody else's
                // record, which is the check working; a fresh name is the answer, and the counter
                // above has already moved on past the one that was taken.
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failure = new RecycleFailure(target.InputPath, ex.HResult, 0, RecycleFailureStage.Performing);
                return false;
            }

            try
            {
                if (isDirectory)
                {
                    Directory.Move(target.FullPath, payloadPath);
                }
                else
                {
                    File.Move(target.FullPath, payloadPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // The record names an item that is still where the caller left it. It goes back before
                // the failure does.
                TryDeleteIndex(indexPath);

                // A move refused because the name was taken is the same collision as above, arriving from
                // the other end — an orphaned payload with no record of its own. It gets a fresh name for
                // the same reason and costs the caller nothing.
                if (attempt < NameCollision.NameAttempts && NameCollision.IsTaken(ex))
                {
                    continue;
                }

                failure = new RecycleFailure(target.InputPath, ex.HResult, 0, RecycleFailureStage.Performing);
                return false;
            }

            // The payload was named from the bin directory of the root taken from this very target,
            // so it sits inside the right bin on the right volume by construction.
            failure = null;
            return true;
        }
    }

    private static void TryDeleteIndex(string indexPath)
    {
        try
        {
            File.Delete(indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The record is left naming an item that never arrived. Nothing above this library can describe
            // it, and the next empty on this volume collects it.
        }
    }

    /// <summary>
    /// Waits out the backoff for the round that just finished.
    /// </summary>
    /// <param name="options">The normalised settings the delay is derived from.</param>
    /// <param name="round">The round that just finished, counting from one; the delay doubles on each.</param>
    /// <param name="stopwatch">The batch's own clock, which <paramref name="deadline"/> was measured against.</param>
    /// <param name="deadline">
    /// When the batch's retry budget runs out. Taken once by the caller rather than derived here, because
    /// the budget is the batch's and a fresh one per round would not bound anything.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, ending the batch rather than faulting inside it.</param>
    /// <returns>
    /// <see langword="false"/> when no further round may start: the overall budget is spent, or the backoff
    /// does not fit in what is left of it.
    /// </returns>
    private static bool WaitBeforeRetry(
        RecycleBinOptions options,
        int round,
        Stopwatch stopwatch,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var exponential = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, round - 1);
        var jittered = exponential * (1 + ((Random.Shared.NextDouble() * 2 - 1) * MaxJitterFraction));

        var delay = TimeSpan.FromMilliseconds(Math.Clamp(jittered, 0, MaxWaitableMilliseconds));

        var remaining = deadline - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero || delay >= remaining)
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
            // The caller disposed their token source while this batch was still queued. They have gone, so
            // this batch has nobody left to report to either.
            return false;
        }
    }
}
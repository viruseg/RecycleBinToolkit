using System.Diagnostics;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves paths into <c>$RECYCLE.BIN</c> by writing the record and moving the payload, with no Shell, no
/// apartment and no staging.
/// </summary>
/// <remarks>
/// Synchronous, because everything it does blocks: a file move is a metadata operation the thread waits
/// on. It is only ever given a thread from the pool, never a caller's and never the Shell worker's.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class NativeRecycleRunner
{
    /// <summary>Longest wait that can be expressed as a <see cref="TimeSpan"/> from milliseconds.</summary>
    private const double MaxWaitableMilliseconds = (double)int.MaxValue;

    /// <summary>
    /// How far a delay is randomised, as a fraction of itself. Stops a batch of locked files from retrying
    /// in lockstep with every other batch on the machine.
    /// </summary>
    private const double MaxJitterFraction = 0.2;

    /// <summary>
    /// How many names one path is given before a collision is taken to be a real failure.
    /// </summary>
    /// <remarks>
    /// Only a name the filesystem reports as taken counts, and only this many of them: the probability of
    /// one is about two in a billion, and a refusal that is <em>not</em> a collision — a locked file, a
    /// denied directory — is reported on its first attempt rather than retried here.
    /// </remarks>
    private const int NameAttempts = 3;

    /// <summary>Win32 <c>ERROR_FILE_EXISTS</c>, returned by a create that would overwrite a file.</summary>
    private const int ErrorFileExists = 80;

    /// <summary>Win32 <c>ERROR_ALREADY_EXISTS</c>, the other half of the same refusal.</summary>
    private const int ErrorAlreadyExists = 183;

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
    /// <b>One thread, deliberately.</b> Every record written and every payload moved here goes into a
    /// single directory — <c>&lt;volume&gt;$RECYCLE.BIN\&lt;SID&gt;\</c> — and a directory is the one thing
    /// a filesystem cannot take concurrent writers on. Measured on this machine over a three-thousand file
    /// batch, recycling three thousand paths took 542 files per second on one thread and 189 on eight, on a
    /// solid-state volume: two and a half times worse, and worse still at every step between. The same
    /// batch read back through the same code gains four and a half times from eight threads, because reading
    /// a record changes nothing. That is the difference this measurement is about, and it is why the
    /// parallelism that <see cref="NativeBinLister"/> uses is absent here.
    /// <para>
    /// Retries are per path rather than per batch, which is the other difference from the Shell path. A
    /// Shell failure is often a property of the whole operation and answering it again means answering about
    /// every item; a move that fails is a property of one path, and retrying the others would repeat work
    /// that already succeeded.
    /// </para>
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
            return RecycleBinResult.Create([], [], stopwatch.Elapsed);
        }

        var collapsed = RecycleOperationRunner.CollapseEquivalentPaths(targets);
        var pending = collapsed;
        var recycled = new List<string>(collapsed.Count);
        var failuresByPath = new Dictionary<string, RecycleFailure>(collapsed.Count, StringComparer.OrdinalIgnoreCase);

        // One bin directory per volume, created at most once for the whole operation. Every path on a
        // volume needs it, and asking the filesystem to make a directory that already exists is a round
        // trip that answers "yes" — twenty thousand of them on a twenty-thousand-path batch.
        var bins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Taken once, before the loop, because the budget it bounds is the batch's and not each round's.
        // Added here rather than inside the wait it is checked by: a deadline recomputed there is a fresh
        // budget every round, and the loop then runs to MaxAttempts whenever one delay fits inside it — which
        // is the case a caller who set a budget at all is the one that hits.
        var deadline = stopwatch.Elapsed + options.OverallTimeout;

        for (var round = 1; round <= options.MaxAttempts && pending.Count > 0; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stillPending = new List<RecycleTarget>(pending.Count);

            foreach (var target in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryRecycle(target, bins, options, out var failure))
                {
                    recycled.Add(target.InputPath);
                    continue;
                }

                stillPending.Add(target);
                failuresByPath[failure!.Path] = new RecycleFailure(failure.Path, failure.HResult, round, failure.Stage);
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

        return RecycleBinResult.Create(recycled, [], reported, stopwatch.Elapsed);
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
    private static bool TryRecycle(RecycleTarget target, Dictionary<string, string> bins, RecycleBinOptions options, out RecycleFailure? failure)
    {
        var root = Path.GetPathRoot(target.ShellPath);

        if (string.IsNullOrEmpty(root))
        {
            failure = new RecycleFailure(target.InputPath, 0, 0, RecycleFailureStage.PathValidation);
            return false;
        }

        if (!bins.TryGetValue(root, out var binDirectory))
        {
            binDirectory = NativeBinLayout.EnsureBinDirectory(root);
            bins.Add(root, binDirectory);
        }

        var isDirectory = target.IsDirectory;
        var extension = Path.GetExtension(target.ShellPath);

        Span<char> stem = stackalloc char[NativeBinLayout.StemLength];

        for (var attempt = 1; ; attempt++)
        {
            NativeBinLayout.NextStem(stem);

            var name = new string(stem);
            var indexPath = NativeBinLayout.IndexPath(binDirectory, name, extension);
            var payloadPath = NativeBinLayout.PayloadPath(binDirectory, name, extension);

            try
            {
                // No query at all. Validation already read the size, and it read it from the same query
                // that told it the path is a file — which is why the record costs nothing to produce beyond
                // the write itself.
                NativeIndexFile.Write(indexPath, target.ShellPath, target.Size, DateTime.UtcNow);
            }
            catch (IOException ex) when (attempt < NameAttempts && IsNameTaken(ex))
            {
                // The record's name was taken. CreateNew refused rather than overwriting somebody else's
                // record, which is the check working; a fresh name is the answer, and generating one costs
                // six random numbers.
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
                // The record names an item that is still where the caller left it, which the Shell would show
                // as a phantom a user could try and fail to restore. It goes back before the failure does.
                TryDeleteIndex(indexPath);

                // A move refused because the name was taken is the same collision as above, arriving from
                // the other end — an orphaned payload with no record of its own. It gets a fresh name for
                // the same reason and costs the caller nothing.
                if (attempt < NameAttempts && ex is IOException io && IsNameTaken(io))
                {
                    continue;
                }

                failure = new RecycleFailure(target.InputPath, ex.HResult, 0, RecycleFailureStage.Performing);
                return false;
            }

            if (options.VerifyRecycleBinReceipt && !IsRecycled(payloadPath, target))
            {
                // The record stays. The payload has already moved and this is the only place where that has
                // happened, so the entry is real and Explorer's Restore works on it: an entry with a record is
                // recoverable and a caller can act on it, while the same entry without one is a file on disk
                // that nothing lists, nothing empties and nobody will ever find.
                failure = new RecycleFailure(target.InputPath, 0, 0, RecycleFailureStage.ReceiptVerification);
                return false;
            }

            failure = null;
            return true;
        }
    }

    /// <summary>
    /// Whether the payload was aimed at a Recycle Bin on the item's own volume.
    /// </summary>
    private static bool IsRecycled(string payloadPath, RecycleTarget target)
    {
        // A check of the name, not of the disk, and the difference is deliberate.
        //
        // File.Move and Directory.Move either move or throw — there is no version of either that reports
        // success without moving — so asking whether the payload is there afterwards is asking the
        // filesystem to confirm what it just said, and it costs a round trip on every path in the batch to
        // learn nothing.
        //
        // The failure RecycleBinOptions.VerifyRecycleBinReceipt exists to catch is the Shell's: a delete
        // past MAX_PATH that removes the file for good and reports success anyway. There is no such failure
        // here, because this backend chose the destination itself rather than asking for one. What remains
        // worth checking is that the destination it chose is inside the bin on the right volume, which is a
        // question about two strings and costs nothing.
        return RecycleBinReceipt.IsInsideRecycleBin(payloadPath, target.ShellPath);
    }

    /// <summary>
    /// Whether a refusal was this name being taken rather than the item being unavailable.
    /// </summary>
    /// <remarks>
    /// Telling the two apart is what lets a collision be retried on a fresh name without a locked file being
    /// retried too. A locked file is a fact about the caller's data and is reported on the first attempt; a
    /// taken name is a fact about this library's own generator and is its own business.
    /// </remarks>
    private static bool IsNameTaken(IOException failure) =>
        failure.HResult is ErrorFileExists or ErrorAlreadyExists;

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
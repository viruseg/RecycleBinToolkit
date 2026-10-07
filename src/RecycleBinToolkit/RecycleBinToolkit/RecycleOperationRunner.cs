using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Win32.UI.Shell;

namespace RecycleBinToolkit;

/// <summary>
/// Runs a batch against the Shell, retrying only the items that actually failed.
/// </summary>
/// <remarks>
/// One round builds Shell items for the unfinished targets, queues them against a sink keyed on
/// exactly those paths, and performs once. An item counts as recycled when the Shell cleared the
/// failure bit on <c>hrDelete</c> and the file is gone from disk; when the Shell also reported where
/// the file went, that location has to be inside the Recycle Bin. Everything else is re-queued on
/// the next round, so a single locked file in a large batch does not cost the other files their
/// first attempt.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class RecycleOperationRunner
{
    private const int SuccessSeverityMask = unchecked((int)0x80000000);
    private const double MaxJitterFraction = 0.25;
    private const double MaxDelayMilliseconds = 9.0e12;

    /// <summary>How many times a rename into the staging directory is attempted before the path is refused.</summary>
    private const int StageAttempts = 4;

    private static readonly TimeSpan StageRetryDelay = TimeSpan.FromMilliseconds(60);

    /// <summary>Value meaning "this item has no verdict yet". Never a real Shell code.</summary>
    private const int NoVerdict = 0;

    /// <summary>Win32 <c>ERROR_FILENAME_EXCED_RANGE</c>, used to mark a path the Shell cannot recycle.</summary>
    private const int PathTooLong = unchecked((int)0x800700CE);

    private readonly ShellOperationContext _context;
    private readonly Stopwatch _stopwatch = new();

    /// <summary>
    /// Scratch space for one round's Shell-item indices and queued paths.
    /// </summary>
    /// <remarks>
    /// A retry loop runs up to <see cref="RecycleBinOptions.MaxAttempts"/> rounds over a shrinking
    /// batch, and every round walked the batch twice through two freshly grown lists. The buffers are
    /// kept and refilled instead. The runner belongs to the single worker thread and serves one request
    /// at a time, so holding them across rounds needs no synchronisation, and nothing outlives the
    /// batch that sized them.
    /// </remarks>
    private sealed class RoundScratch
    {
        private int[] _buildable = [];
        private int[] _queueable = [];
        private string[] _queuedPaths = [];
        private bool[] _succeeded = [];
        private IShellItem?[] _items = [];

        internal int[] Buildable => _buildable;

        internal int[] Queueable => _queueable;

        internal string[] QueuedPaths => _queuedPaths;

        /// <summary>Per-item verdicts for the round about to run.</summary>
        internal bool[] Succeeded => _succeeded;

        /// <summary>The Shell item for each target, or <see langword="null"/> where none was built.</summary>
        internal IShellItem?[] Items => _items;

        /// <summary>Grows the buffers so a batch of <paramref name="count"/> items fits without resizing mid-round.</summary>
        internal void EnsureCapacity(int count)
        {
            if (_buildable.Length < count)
            {
                _buildable = new int[count];
                _queueable = new int[count];
                _queuedPaths = new string[count];
                _succeeded = new bool[count];
                _items = new IShellItem?[count];
            }
        }

        /// <summary>
        /// Prepares the buffers for a round over <paramref name="count"/> targets.
        /// </summary>
        /// <remarks>
        /// The item buffer is cleared rather than assumed empty because a round writes only where a
        /// Shell item was built. Without this, a target the Shell refuses would read as carrying the
        /// previous round's item, and the stale reference would also keep that item's apartment object
        /// alive for as long as the runner is.
        /// </remarks>
        internal void BeginRound(int count)
        {
            EnsureCapacity(count);

            Array.Clear(_succeeded, 0, count);
            Array.Clear(_items, 0, count);
        }
    }

    internal RecycleOperationRunner(ShellOperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <summary>
    /// Attempts every target until it is recycled or the attempt and time budgets run out.
    /// </summary>
    /// <param name="targets">Paths to recycle. Duplicates are collapsed to their first occurrence.</param>
    /// <param name="options">Retry and verification settings; assumed already normalised.</param>
    /// <param name="cancellationToken">
    /// Stops the loop between rounds. A round already handed to the Shell always runs to
    /// completion, because abandoning it mid-flight would leave the operation queue in an unknown
    /// state.
    /// </param>
    /// <returns>The outcome of the batch. Never throws for a per-item failure.</returns>
    internal RecycleBinResult Run(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        _stopwatch.Restart();

        if (targets.Count == 0)
        {
            return RecycleBinResult.Create([], [], _stopwatch.Elapsed);
        }

        // Scoped to this operation: a staging directory exists only as long as something is staged
        // in it, and is removed before returning.
        var stager = new LongPathStager();
        try
        {
            return RunOperation(targets, options, cancellationToken, stager);
        }
        finally
        {
            stager.Dispose();
        }
    }

    private RecycleBinResult RunOperation(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken,
        LongPathStager stager)
    {
        var pending = Deduplicate(targets);
        var recycled = new List<string>(pending.Count);
        var permanentlyDeleted = new List<string>();
        var failures = new List<RecycleFailure>();

        // Split off the paths the Shell must never see. Handing it a path past its limit makes it
        // remove the file permanently while reporting success, so the decision has to happen before
        // the first round rather than after the damage.
        var (recyclable, unrecyclable) = PartitionByRecyclability(pending, options);
        var staged = new List<StagedTarget>();
        HandleUnrecyclable(unrecyclable, options, permanentlyDeleted, staged, stager, failures);

        pending = staged.Count == 0
            ? recyclable
            : [.. recyclable, .. staged.Select(entry => entry.Target)];

        var attempts = new int[pending.Count];
        var lastHResult = new int[pending.Count];
        var lastStage = new RecycleFailureStage[pending.Count];
        var scratch = new RoundScratch();
        var deadline = _stopwatch.Elapsed + options.OverallTimeout;

        for (var round = 1; round <= options.MaxAttempts && pending.Count > 0; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = RunRound(pending, options, lastHResult, lastStage, scratch);

            for (var i = 0; i < pending.Count; i++)
            {
                attempts[i] = round;

                if (outcome.Succeeded[i])
                {
                    recycled.Add(pending[i].InputPath);
                }
            }

            var stillPending = new List<RecycleTarget>(pending.Count);
            for (var i = 0; i < pending.Count; i++)
            {
                if (!outcome.Succeeded[i])
                {
                    stillPending.Add(pending[i]);
                }
            }

            pending = stillPending;

            if (pending.Count == 0 || round == options.MaxAttempts)
            {
                break;
            }

            if (!WaitBeforeRetry(options, round, deadline, cancellationToken))
            {
                break;
            }
        }

        for (var i = 0; i < pending.Count; i++)
        {
            failures.Add(new RecycleFailure(pending[i].InputPath, lastHResult[i], attempts[i], lastStage[i]));
        }

        RestoreUnrecycledStagedItems(staged, recyclable);
        var leftover = stager.Release();

        return RecycleBinResult.Create(recycled, permanentlyDeleted, failures, _stopwatch.Elapsed, leftover);
    }

    /// <summary>
    /// Puts back anything that was moved aside and then failed to recycle, so a failed operation
    /// leaves the caller's files where it found them.
    /// </summary>
    private static void RestoreUnrecycledStagedItems(List<StagedTarget> staged, List<RecycleTarget> recyclable)
    {
        if (staged.Count == 0)
        {
            return;
        }

        var recycledPaths = new HashSet<string>(recyclable.Select(target => target.InputPath), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in staged)
        {
            if (!recycledPaths.Contains(entry.Target.InputPath))
            {
                LongPathStager.TryRestore(entry.Target.FullPath, entry.OriginalFullPath);
            }
        }
    }

    /// <summary>
    /// Splits the batch into the paths the Shell can be asked to recycle and the ones it must not see.
    /// A path within the limit goes straight through. A longer one is only salvageable when the volume
    /// offers an 8.3 alias short enough for the Shell to accept.
    /// </summary>
    private static (List<RecycleTarget> Recyclable, List<RecycleTarget> Unrecyclable) PartitionByRecyclability(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options)
    {
        var recyclable = new List<RecycleTarget>(targets.Count);
        var unrecyclable = new List<RecycleTarget>();

        foreach (var target in targets)
        {
            if (target.ShellPath.Length <= options.MaxShellPathLength)
            {
                recyclable.Add(target);
                continue;
            }

            var shortPath = ShortPathResolver.TryResolveShort(target.ShellPath);
            if (shortPath is not null && shortPath.Length <= options.MaxShellPathLength)
            {
                recyclable.Add(target.WithShellPath(shortPath));
                continue;
            }

            unrecyclable.Add(target);
        }

        return (recyclable, unrecyclable);
    }

    /// <summary>
    /// Applies the caller's policy to every path the Shell must not see, recording the ones it
    /// cannot rescue as failures.
    /// </summary>
    /// <remarks>
    /// Writes straight into the caller's lists rather than yielding a sequence for the caller to
    /// consume. This runs before the retry loop and on the batch's one long-path pass, so the
    /// iterator's state machine was one allocation for a loop that already had every list it needed
    /// in hand.
    /// </remarks>
    /// <param name="unrecyclable">Paths past the Shell's length limit, in batch order.</param>
    /// <param name="options">Retry, verification and long-path settings; assumed already normalised.</param>
    /// <param name="permanentlyDeleted">Receives the paths removed for good, if the caller opted in to that.</param>
    /// <param name="staged">Receives the paths moved aside to a short path so the Shell can take them.</param>
    /// <param name="stager">Moves items to the staging directory and cleans it up afterwards.</param>
    /// <param name="failures">Receives one failure per path the policy could not act on.</param>
    private static void HandleUnrecyclable(
        List<RecycleTarget> unrecyclable,
        RecycleBinOptions options,
        List<string> permanentlyDeleted,
        List<StagedTarget> staged,
        LongPathStager stager,
        List<RecycleFailure> failures)
    {
        var awaitingStaging = new List<RecycleTarget>();

        foreach (var target in unrecyclable)
        {
            if (options.LongPathFailureAction == LongPathFailureAction.DeletePermanently)
            {
                if (TryDeletePermanently(target, permanentlyDeleted, out var refused))
                {
                    continue;
                }

                // The file system gave a reason, so report that rather than blaming the path length.
                if (refused != 0)
                {
                    failures.Add(new RecycleFailure(target.InputPath, refused, 0, RecycleFailureStage.PermanentDeleteFailed));
                    continue;
                }
            }

            if (options.LongPathFailureAction == LongPathFailureAction.ShortenPathAndRecycle)
            {
                awaitingStaging.Add(target);
                continue;
            }

            failures.Add(new RecycleFailure(target.InputPath, PathTooLong, 0, RecycleFailureStage.LongPathNotRecyclable));
        }

        StageWithBatchRetries(awaitingStaging, staged, stager, failures);
    }

    /// <summary>
    /// Stages a batch of long paths, giving every one of them an attempt before any of them waits.
    /// </summary>
    /// <remarks>
    /// A rename that loses a race with the indexer or antivirus is worth retrying, and the tests provoke
    /// exactly that by creating and recycling thousands of files in one directory. Retrying per path
    /// means a contended batch pays the whole backoff once per item, on the one thread every other Shell
    /// caller queues behind. Attempting the batch first and only then waiting turns that into a single
    /// backoff for the batch, at the same cost to each individual path.
    /// </remarks>
    private static void StageWithBatchRetries(
        List<RecycleTarget> awaitingStaging,
        List<StagedTarget> staged,
        LongPathStager stager,
        List<RecycleFailure> failures)
    {
        for (var attempt = 1; attempt <= StageAttempts && awaitingStaging.Count > 0; attempt++)
        {
            if (attempt > 1)
            {
                Thread.Sleep(StageRetryDelay);
            }

            var stillWaiting = new List<RecycleTarget>();

            foreach (var target in awaitingStaging)
            {
                var stagedPath = stager.TryStageOnce(target.FullPath);
                if (stagedPath is not null)
                {
                    staged.Add(new StagedTarget(target.WithShellPath(stagedPath), target.FullPath));
                }
                else
                {
                    stillWaiting.Add(target);
                }
            }

            awaitingStaging = stillWaiting;
        }

        foreach (var target in awaitingStaging)
        {
            failures.Add(new RecycleFailure(target.InputPath, PathTooLong, 0, RecycleFailureStage.LongPathNotRecyclable));
        }
    }

    /// <summary>
    /// Removes a path the Shell could not recycle, reporting it as permanently deleted only once the
    /// file system confirms it is gone.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the path is gone. On <see langword="false"/>,
    /// <paramref name="refused"/> carries the file system's own error code, or 0 when nothing was raised
    /// and the path is nonetheless still there, in which case the caller has nothing better to say than
    /// that the path could not be handled.
    /// </returns>
    private static bool TryDeletePermanently(RecycleTarget target, List<string> permanentlyDeleted, out int refused)
    {
        try
        {
            File.Delete(target.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            refused = ex.HResult;
            return false;
        }

        if (File.Exists(target.FullPath) || Directory.Exists(target.FullPath))
        {
            refused = 0;
            return false;
        }

        permanentlyDeleted.Add(target.InputPath);
        refused = 0;
        return true;
    }

    /// <summary>
    /// An item moved to a short path, with where it came from so it can be put back if the recycle
    /// does not work out.
    /// </summary>
    private readonly struct StagedTarget(RecycleTarget target, string originalFullPath)
    {
        internal RecycleTarget Target { get; } = target;

        internal string OriginalFullPath { get; } = originalFullPath;
    }

    private RoundOutcome RunRound(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        int[] lastHResult,
        RecycleFailureStage[] lastStage,
        RoundScratch scratch)
    {
        scratch.BeginRound(targets.Count);
        var buildable = scratch.Buildable;
        var queuedPaths = scratch.QueuedPaths;
        var items = scratch.Items;
        var succeeded = scratch.Succeeded;
        var buildableCount = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            if (_context.TryCreateShellItem(targets[i].ShellPath, out var item, out var itemHResult))
            {
                items[i] = item;
                buildable[buildableCount++] = i;
            }
            else
            {
                RecordVerdict(lastHResult, lastStage, i, itemHResult, RecycleFailureStage.ShellItemCreation);
            }
        }

        // Keyed on exactly the paths that can reach the Shell. Including a path whose Shell item
        // could not be built would let the sink's queue-position fallback hand its slot to a
        // different item's result. Only the first buildableCount entries are queued this round.
        for (var i = 0; i < buildableCount; i++)
        {
            queuedPaths[i] = targets[buildable[i]].ShellPath;
        }

        var sink = new RecycleProgressSink(queuedPaths, buildableCount);
        var queueable = scratch.Queueable;
        var queueableCount = 0;

        _context.SetFlags();

        for (var q = 0; q < buildableCount; q++)
        {
            var i = buildable[q];

            if (_context.TryQueue(items[i]!, sink, out var queueHResult))
            {
                queueable[queueableCount++] = i;
            }
            else
            {
                RecordVerdict(lastHResult, lastStage, i, queueHResult, RecycleFailureStage.Queueing);
            }
        }

        if (queueableCount > 0)
        {
            _context.Perform();
        }

        for (var q = 0; q < queueableCount; q++)
        {
            var i = queueable[q];
            var target = targets[i];

            if (!sink.TryGetOutcome(target.ShellPath, out var hResult, out var newItemPath))
            {
                // Queued but the Shell never reported on it: unfinished, not a success.
                RecordVerdict(lastHResult, lastStage, i, hResult, RecycleFailureStage.Performing);
                continue;
            }

            var stage = ClassifyOutcome(
                hResult,
                Exists(target.FullPath),
                newItemPath,
                options.VerifyRecycleBinReceipt,
                target.FullPath);

            if (stage is not null)
            {
                RecordVerdict(lastHResult, lastStage, i, hResult, stage.Value);
                continue;
            }

            succeeded[i] = true;
        }

        return new RoundOutcome(succeeded);
    }

    /// <summary>
    /// Decides what one item's Shell result means.
    /// </summary>
    /// <param name="hResult">The Shell's <c>hrDelete</c> for the item.</param>
    /// <param name="stillOnDisk">Whether the path is still there after the round.</param>
    /// <param name="recycleBinPath">Path the Shell reported as created, or <see langword="null"/>.</param>
    /// <param name="verifyReceipt">Whether a reported destination is checked against the Recycle Bin.</param>
    /// <param name="originalPath">Path the item was deleted from.</param>
    /// <returns>
    /// The stage to report, or <see langword="null"/> when the item counts as recycled.
    /// </returns>
    /// <remarks>
    /// The verdict for an item is written once, when it first fails, and the earliest reason is the one
    /// kept: a file the Shell already refused often disappears before the next round, and the follow-up
    /// "file not found" is a consequence of the refusal rather than its cause. Within one round the stage
    /// has to be the most specific one, which is why the general one is not recorded before the receipt
    /// has been consulted — first-write-wins would otherwise drop it every time.
    /// </remarks>
    internal static RecycleFailureStage? ClassifyOutcome(
        int hResult,
        bool stillOnDisk,
        string? recycleBinPath,
        bool verifyReceipt,
        string originalPath)
    {
        if ((hResult & SuccessSeverityMask) != 0 || stillOnDisk)
        {
            return RecycleFailureStage.Performing;
        }

        // The Shell reported success and where the file went, and that was not the Recycle Bin: the file
        // was removed permanently.
        if (verifyReceipt && recycleBinPath is not null && !RecycleBinReceipt.IsInsideRecycleBin(recycleBinPath, originalPath))
        {
            return RecycleFailureStage.ReceiptVerification;
        }

        return null;
    }

    /// <summary>
    /// Keeps the first verdict that carries a real Shell code. A file the Shell already refused
    /// often disappears before the next round — removed by another process, quarantined by
    /// Defender — and the follow-up "file not found" is a consequence of the refusal, not its
    /// cause, so reporting it would send an integrator looking for the wrong problem.
    /// </summary>
    private static void RecordVerdict(
        int[] lastHResult,
        RecycleFailureStage[] lastStage,
        int index,
        int hResult,
        RecycleFailureStage stage)
    {
        var existing = lastHResult[index];
        if (existing != NoVerdict && existing != RecycleProgressSink.Unreported)
        {
            return;
        }

        lastHResult[index] = hResult;
        lastStage[index] = stage;
    }

    /// <summary>
    /// Waits out the backoff for the round that just finished.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when no further round may start: either the overall budget is spent,
    /// or the backoff does not fit in what is left of it. Sleeping the remainder and then running
    /// an over-budget round is the one thing <see cref="RecycleBinOptions.OverallTimeout"/> exists to
    /// prevent, and deciding that up front also keeps the choice off the boundary: clamping the sleep
    /// to the remaining budget makes "have we reached the deadline" a coin flip.
    /// </returns>
    private bool WaitBeforeRetry(
        RecycleBinOptions options,
        int round,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        var exponential = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, round - 1);
        var jittered = exponential * (1 + ((Random.Shared.NextDouble() * 2 - 1) * MaxJitterFraction));
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(jittered, 0, MaxDelayMilliseconds));

        var remaining = deadline - _stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero || delay >= remaining)
        {
            return false;
        }

        cancellationToken.WaitHandle.WaitOne(delay);
        return true;
    }

    private static IReadOnlyList<RecycleTarget> Deduplicate(IReadOnlyList<RecycleTarget> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<RecycleTarget>(targets.Count);

        foreach (var target in targets)
        {
            if (seen.Add(target.ShellPath))
            {
                unique.Add(target);
            }
        }

        return unique;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private readonly struct RoundOutcome(bool[] succeeded)
    {
        internal bool[] Succeeded { get; } = succeeded;
    }
}
using System.Runtime.Versioning;

namespace Utils;

/// <summary>
/// Runs a batch against the Shell, retrying only the items that actually failed.
/// </summary>
/// <remarks>
/// One round queues every unfinished target and performs once. An item counts as recycled when the
/// Shell cleared the failure bit on <c>hrDelete</c> and the file is gone from disk; when the Shell
/// also reported where the file went, that location has to be inside the Recycle Bin. Everything
/// else is re-queued on the next round, so a single locked file in a large batch does not cost the
/// other files their first attempt.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class RecycleOperationRunner
{
    private const int SuccessSeverityMask = unchecked((int)0x80000000);
    private const double MaxJitterFraction = 0.25;

    private readonly ShellOperationContext _context;

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

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        if (targets.Count == 0)
        {
            return RecycleBinResult.Create([], [], stopwatch.Elapsed);
        }

        var pending = Deduplicate(targets);
        var recycled = new List<string>(pending.Count);
        var failures = new List<RecycleFailure>();
        var attempts = new int[pending.Count];
        var lastHResult = new int[pending.Count];
        var lastStage = new RecycleFailureStage[pending.Count];
        var deadline = stopwatch.Elapsed + options.OverallTimeout;

        for (var round = 1; round <= options.MaxAttempts && pending.Count > 0; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = RunRound(pending, options, lastHResult, lastStage);

            for (var i = 0; i < pending.Count; i++)
            {
                var target = pending[i];
                attempts[i] = round;

                if (!outcome.Succeeded[i])
                {
                    continue;
                }

                recycled.Add(target.InputPath);
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

            if (pending.Count == 0 || round == options.MaxAttempts || stopwatch.Elapsed >= deadline)
            {
                break;
            }

            WaitBeforeRetry(options, round, deadline, cancellationToken);
        }

        for (var i = 0; i < pending.Count; i++)
        {
            failures.Add(new RecycleFailure(pending[i].InputPath, lastHResult[i], attempts[i], lastStage[i]));
        }

        return RecycleBinResult.Create(recycled, failures, stopwatch.Elapsed);
    }

    private RoundOutcome RunRound(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        int[] lastHResult,
        RecycleFailureStage[] lastStage)
    {
        var succeeded = new bool[targets.Count];
        var paths = new string[targets.Count];
        var queueable = new List<int>(targets.Count);

        for (var i = 0; i < targets.Count; i++)
        {
            paths[i] = targets[i].FullPath;
        }

        _context.SetFlags();

        var sink = new RecycleProgressSink(paths);

        for (var i = 0; i < targets.Count; i++)
        {
            if (!_context.TryCreateShellItem(targets[i].FullPath, out var item, out var itemHResult))
            {
                lastHResult[i] = itemHResult;
                lastStage[i] = RecycleFailureStage.ShellItemCreation;
                continue;
            }

            if (_context.TryQueue(item!, sink, out var queueHResult))
            {
                queueable.Add(i);
            }
            else
            {
                lastHResult[i] = queueHResult;
                lastStage[i] = RecycleFailureStage.Queueing;
            }
        }

        if (queueable.Count > 0)
        {
            _context.Perform();
        }

        for (var position = 0; position < queueable.Count; position++)
        {
            var i = queueable[position];
            var target = targets[i];

            if (!sink.TryGetOutcome(target.FullPath, out var hResult, out var newItemPath))
            {
                // Queued but the Shell never reported on it: treat as unfinished rather than
                // inventing a success.
                lastHResult[i] = hResult;
                lastStage[i] = RecycleFailureStage.Performing;
                continue;
            }

            lastHResult[i] = hResult;
            lastStage[i] = RecycleFailureStage.Performing;

            if ((hResult & SuccessSeverityMask) != 0)
            {
                continue;
            }

            if (Exists(target.FullPath))
            {
                continue;
            }

            if (options.VerifyRecycleBinReceipt && newItemPath is not null && !RecycleBinReceipt.IsInsideRecycleBin(newItemPath, target.FullPath))
            {
                // The Shell reported success and where the file went, and that was not the Recycle
                // Bin: the file was removed permanently.
                lastStage[i] = RecycleFailureStage.ReceiptVerification;
                continue;
            }

            succeeded[i] = true;
        }

        return new RoundOutcome(succeeded);
    }

    private static void WaitBeforeRetry(
        RecycleBinOptions options,
        int round,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var exponential = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, round - 1);
        var jittered = exponential * (1 + ((Random.Shared.NextDouble() * 2 - 1) * MaxJitterFraction));
        var delay = TimeSpan.FromMilliseconds(Math.Max(0, jittered));

        var remaining = elapsed - System.Diagnostics.Stopwatch.StartNew().Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        if (delay > remaining)
        {
            delay = remaining;
        }

        cancellationToken.WaitHandle.WaitOne(delay);
    }

    private static IReadOnlyList<RecycleTarget> Deduplicate(IReadOnlyList<RecycleTarget> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<RecycleTarget>(targets.Count);

        foreach (var target in targets)
        {
            if (seen.Add(target.FullPath))
            {
                unique.Add(target);
            }
        }

        return unique;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private readonly struct RoundOutcome
    {
        internal RoundOutcome(bool[] succeeded) => Succeeded = succeeded;

        internal bool[] Succeeded { get; }
    }
}
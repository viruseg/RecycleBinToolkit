using System.Diagnostics;
using System.Reflection;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class RunnerSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("RunnerSuite." + nameof(TransientLockIsRetriedUntilSuccess), TransientLockIsRetriedUntilSuccess),
        new TestCase("RunnerSuite." + nameof(PermanentlyLockedFileIsReportedNotThrown), PermanentlyLockedFileIsReportedNotThrown),
        new TestCase("RunnerSuite." + nameof(DuplicatePathsAreNotReportedAsFailures), DuplicatePathsAreNotReportedAsFailures),
        new TestCase("RunnerSuite." + nameof(ReceiptRecognisesOnlyRecycleBinPaths), ReceiptRecognisesOnlyRecycleBinPaths),
        new TestCase("RunnerSuite." + nameof(OverallTimeoutStopsFurtherRounds), OverallTimeoutStopsFurtherRounds),
        new TestCase("RunnerSuite." + nameof(FirstShellVerdictSurvivesALaterMissingPath), FirstShellVerdictSurvivesALaterMissingPath),
        new TestCase("RunnerSuite." + nameof(EachFailureKeepsItsOwnVerdictAcrossRounds), EachFailureKeepsItsOwnVerdictAcrossRounds),
        new TestCase("RunnerSuite." + nameof(TheBackoffSurvivesADisposedTokenSource), TheBackoffSurvivesADisposedTokenSource),
    ];

    /// <summary>
    /// A caller that disposes its token source while its batch is still queued must not have the retry
    /// loop fault on the way out. <see cref="CancellationToken.WaitHandle"/> throws once the source is
    /// disposed, and the token still reports itself cancellable afterwards, so a check on
    /// <see cref="CancellationToken.CanBeCanceled"/> does not keep that away.
    /// </summary>
    /// <remarks>
    /// Reachable in ordinary code: the caller's wait ends on cancellation, its <c>using</c> disposes the
    /// source, and the worker is still sitting in its backoff — so the next backoff is the one that
    /// faults, into a task nobody is reading any more, silently ending a batch that had work left.
    /// </remarks>
    private static Task TheBackoffSurvivesADisposedTokenSource(TestContext context)
    {
        using var shell = ShellOperationContext.Create();
        var runner = new RecycleOperationRunner(shell);

        var options = new RecycleBinOptions
        {
            MaxAttempts = 4,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            OverallTimeout = TimeSpan.FromSeconds(30)
        };

        var deadline = TimeSpan.FromSeconds(30);

        // A token nobody can cancel has no handle at all, which is the case the branch already covered.
        Check.True(
            WaitBeforeRetry(runner, options, 1, deadline, CancellationToken.None),
            "a wait with no token to honour still happens");

        var cts = new CancellationTokenSource();

        // Taken while the source is alive, which is what a caller does: the token is read once and the
        // source is disposed afterwards, from a using block or a finally.
        var orphaned = cts.Token;

        cts.Cancel();
        cts.Dispose();

        // Must not throw, and must report that no further round should start: the caller's token is
        // gone, so this batch has nobody left to finish it for.
        Check.False(
            WaitBeforeRetry(runner, options, 1, deadline, orphaned),
            "a disposed source ends the batch rather than faulting inside the wait");

        Check.True(
            WaitBeforeRetry(runner, options, 1, deadline, CancellationToken.None),
            "and the runner keeps working afterwards, rather than being left poisoned by the fault");

        return Task.CompletedTask;
    }

    private static bool WaitBeforeRetry(
        RecycleOperationRunner runner,
        RecycleBinOptions options,
        int round,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            return (bool)runner
                .GetType()
                .GetMethod("WaitBeforeRetry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(runner, [options, round, deadline, cancellationToken])!;
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            throw wrapped.InnerException;
        }
    }

    /// <summary>
    /// A retry round works on fewer items than the round before it, so the verdicts recorded for them have
    /// to travel with the item they belong to.
    /// </summary>
    /// <remarks>
    /// The batch here is three items with three different outcomes, and the retry is what makes the
    /// bookkeeping visible: the first item succeeds and leaves the list, so the two that remain slide down
    /// a slot each. A verdict held by position rather than by item is then read back against the wrong
    /// path — the report names one file and gives it another file's error code and stage, which is worse
    /// than no code at all because it sends an integrator after the wrong problem.
    /// <para>
    /// The three are chosen so their codes are unmistakably different: a locked file, which the Shell
    /// refuses with a sharing violation, and a path that is not there, which never becomes a Shell item at
    /// all. Any mix-up between them is visible in the stage alone.
    /// </para>
    /// </remarks>
    private static Task EachFailureKeepsItsOwnVerdictAcrossRounds(TestContext context)
    {
        const int FileNotFound = unchecked((int)0x80070002);

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var files = workspace.CreateRandomFiles(1, 4096);
        var recycled = files[0];
        var locked = workspace.CreateRandomFile(4096);
        var absent = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

        var targets = new List<RecycleTarget>
        {
            new(recycled, recycled, recycled),
            new(locked, locked, locked),
            new(absent, absent, absent)
        };

        var options = new RecycleBinOptions
        {
            MaxAttempts = 2,
            InitialRetryDelay = TimeSpan.Zero,
            OverallTimeout = TimeSpan.FromSeconds(30)
        };

        // Opened without delete sharing, so the Shell refuses this one on every round.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = StaThread.Run(() =>
            {
                using var shell = ShellOperationContext.Create();
                return new RecycleOperationRunner(shell).Run(targets, options, context.CancellationToken);
            });

            Check.False(File.Exists(recycled), "the unlocked file was recycled on the first round");
            Check.True(File.Exists(locked), "the locked file is still there, as the Shell refused it");

            var lockedFailure = result.Failures.FirstOrDefault(f => f.Path == locked);
            Check.NotNull(lockedFailure, "the locked file is reported as a failure");

            var absentFailure = result.Failures.FirstOrDefault(f => f.Path == absent);
            Check.NotNull(absentFailure, "the absent path is reported as a failure");

            Check.Equal(
                RecycleFailureStage.Performing,
                lockedFailure!.Stage,
                "the locked file failed while being performed, and says so");
            Check.Equal(
                RecycleFailureStage.ShellItemCreation,
                absentFailure!.Stage,
                "the absent path never became a Shell item, and says so rather than inheriting the other file's stage");
            Check.NotEqual(
                lockedFailure.HResult,
                absentFailure.HResult,
                "the two failures carry different codes, so neither can be the other's");
            Check.Equal(FileNotFound, absentFailure.HResult, "the absent path carries the code the Shell gave for it");
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <see cref="RecycleBinOptions.OverallTimeout"/> is documented as an upper bound on the
    /// wall-clock time. With a first backoff far longer than the timeout, the runner must not sleep
    /// past the deadline and then start another full round.
    /// </summary>
    private static Task OverallTimeoutStopsFurtherRounds(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var targets = Targets(files);

        // The budget is generous so round one reliably finishes inside it, and the backoff is far
        // longer, so what crosses the deadline is always the wait. A budget a single round could
        // straddle would make the expected attempt count depend on machine load.
        var options = new RecycleBinOptions
        {
            MaxAttempts = 6,
            InitialRetryDelay = TimeSpan.FromSeconds(60),
            OverallTimeout = TimeSpan.FromSeconds(1)
        };

        var sw = Stopwatch.StartNew();

        using (new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = StaThread.Run(() =>
            {
                using var shell = ShellOperationContext.Create();
                return new RecycleOperationRunner(shell).Run(targets, options, context.CancellationToken);
            });

            sw.Stop();

            Check.False(result.Succeeded, "the locked file is still reported as a failure");
            Check.Equal(1, result.Failures.Count, "one failure is reported");
            Check.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"the timeout stops the loop instead of sleeping through the 60 s backoff (took {sw.ElapsedMilliseconds} ms)");
            Check.Equal(
                1,
                result.FirstFailure!.Attempts,
                "no round starts after the deadline, even when the backoff ran right up to it");
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// When a file the Shell already refused disappears before the next round, the recorded failure
    /// must stay the Shell's verdict. Replacing it with "file not found" points an integrator at
    /// the wrong problem.
    /// </summary>
    private static Task FirstShellVerdictSurvivesALaterMissingPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var targets = Targets(files);

        var options = new RecycleBinOptions
        {
            MaxAttempts = 4,
            InitialRetryDelay = TimeSpan.FromMilliseconds(60),
            OverallTimeout = TimeSpan.FromSeconds(20)
        };

        RecycleBinResult result;

        // Hold the file so round 1 is refused by the Shell, then remove it entirely so the next
        // round cannot even build a Shell item for it.
        using (var holder = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var remover = Task.Run(() =>
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(120));
                holder.Dispose();
                File.Delete(files[0]);
            });

            result = StaThread.Run(() =>
            {
                using var shell = ShellOperationContext.Create();
                return new RecycleOperationRunner(shell).Run(targets, options, context.CancellationToken);
            });

            remover.Wait(TimeSpan.FromSeconds(30));
        }

        Check.False(result.Succeeded, "the item is reported as a failure");
        Check.NotNull(result.FirstFailure, "a failure is recorded");
        Check.Equal(
            RecycleFailureStage.Performing,
            result.FirstFailure!.Stage,
            $"the Shell's original verdict is kept, not the later missing-path one (got {result.FirstFailure.Stage} 0x{result.FirstFailure.HResult:X8})");
        Check.Equal(unchecked((int)0x80270027), result.FirstFailure.HResult, "the Shell refusal code is preserved");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task TransientLockIsRetriedUntilSuccess(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var targets = Targets(files);

// Stands in for a search indexer, a preview handler or Defender holding the file for a
        // moment: the lock is already there when the first attempt runs and clears shortly after.
        // The holder opens the file before the runner starts, so the first attempt genuinely sees a
        // blocked file.
        var opened = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Set();
            Thread.Sleep(TimeSpan.FromMilliseconds(300));
        });

        if (!opened.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("the holder thread never opened the file");
        }

        var sw = Stopwatch.StartNew();
        var result = StaThread.Run(() =>
        {
            using var shell = ShellOperationContext.Create();
            return new RecycleOperationRunner(shell).Run(targets, RecycleBinOptions.Default, context.CancellationToken);
        });
        sw.Stop();

        holder.Wait(TimeSpan.FromSeconds(30));

        Console.WriteLine($"        retried for {sw.ElapsedMilliseconds} ms");

        Check.True(result.Succeeded, $"the transient lock is retried away (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(0, result.Failures.Count, "no failure is reported once the lock clears");
        Check.Equal(files.Count, result.RecycledPaths.Count, "the file is reported as recycled");
        Check.False(File.Exists(files[0]), "the file is gone from disk");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task PermanentlyLockedFileIsReportedNotThrown(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(3, 4096);
        var targets = Targets(files);

        var options = new RecycleBinOptions
        {
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            OverallTimeout = TimeSpan.FromSeconds(10)
        };

        RecycleBinResult result;

        // One file stays locked for the whole run, the other two must still be recycled: a batch
        // must not be all-or-nothing.
        using (new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = StaThread.Run(() =>
            {
                using var shell = ShellOperationContext.Create();
                return new RecycleOperationRunner(shell).Run(targets, options, context.CancellationToken);
            });
        }

        Check.False(result.Succeeded, "a permanently locked file makes the batch report failure");
        Check.NotNull(result.FirstFailure, "the failure is reported");
        Check.Equal(3, result.FirstFailure!.Attempts, "the item was attempted the configured number of times");
        Check.Equal(files[0], result.FirstFailure.Path, "the failure points at the locked file");
        Check.Equal(RecycleFailureStage.Performing, result.FirstFailure.Stage, "the failure is attributed to the Shell performing the delete");
        Check.True((result.FirstFailure.HResult & unchecked((int)0x80000000)) != 0, $"the failure carries a Shell error code (0x{result.FirstFailure.HResult:X8})");
        Check.Equal(1, result.Failures.Count, "only the locked file is reported as failed");

        Check.Equal(2, result.RecycledPaths.Count, "the two free files were still recycled");
        Check.False(File.Exists(files[1]), "second file is gone from disk");
        Check.False(File.Exists(files[2]), "third file is gone from disk");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task DuplicatePathsAreNotReportedAsFailures(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);

        var targets = new List<RecycleTarget> { new(files[0], files[0], files[0]), new(files[0], files[0], files[0]) };

        var result = StaThread.Run(() =>
        {
            using var shell = ShellOperationContext.Create();
            return new RecycleOperationRunner(shell).Run(targets, RecycleBinOptions.Default, context.CancellationToken);
        });

        Check.True(result.Succeeded, $"a repeated path is not treated as a failure (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(0, result.Failures.Count, "no failure is reported for the duplicate");
        Check.Equal(1, result.RecycledPaths.Count, "the path is reported once");
        Check.False(File.Exists(files[0]), "the file is gone from disk");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ReceiptRecognisesOnlyRecycleBinPaths(TestContext context)
    {
        const string original = @"E:\RC\Test\a.bin";

        Check.False(RecycleBinReceipt.IsInsideRecycleBin(null, original), "an unreported result is not a receipt");
        Check.True(
            RecycleBinReceipt.IsInsideRecycleBin(@"E:\$RECYCLE.BIN\S-1-5-21-1001\$RABCDEF.bin", original),
            "a Shell-reported path under the volume's \\$RECYCLE.BIN is a receipt");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"E:\RC\Test\a.bin", original),
            "the original path itself is not a receipt");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"E:\RC\Test\backup\a.bin", original),
            "a delete outside the Recycle Bin is not a receipt");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"D:\$RECYCLE.BIN\S-1-5-21-1001\$RABCDEF.bin", original),
            "a Recycle Bin on another volume is not a receipt for this path");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds one target per path, with the path in all three forms.
    /// </summary>
    private static IReadOnlyList<RecycleTarget> Targets(IReadOnlyList<string> files)
    {
        var targets = new List<RecycleTarget>(files.Count);

        foreach (var file in files)
        {
            targets.Add(new RecycleTarget(file, file, file));
        }

        return targets;
    }
}
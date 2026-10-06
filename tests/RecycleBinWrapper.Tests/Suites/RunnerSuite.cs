using RecycleBinWrapper.Tests.Host;
using RecycleBinWrapper.Tests.Infrastructure;
using Utils;

namespace RecycleBinWrapper.Tests.Suites;

internal static class RunnerSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("RunnerSuite." + nameof(TransientLockIsRetriedUntilSuccess), TransientLockIsRetriedUntilSuccess),
        new TestCase("RunnerSuite." + nameof(PermanentlyLockedFileIsReportedNotThrown), PermanentlyLockedFileIsReportedNotThrown),
        new TestCase("RunnerSuite." + nameof(DuplicatePathsAreNotReportedAsFailures), DuplicatePathsAreNotReportedAsFailures),
        new TestCase("RunnerSuite." + nameof(ReceiptRecognisesOnlyRecycleBinPaths), ReceiptRecognisesOnlyRecycleBinPaths),
    ];

    private static Task TransientLockIsRetriedUntilSuccess(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var targets = Targets(files);

// Stands in for a search indexer, a preview handler or Defender holding the file for a moment:
// the lock is already there when the first attempt runs and clears shortly after. The holder opens
// the file before the runner starts, so the first attempt genuinely sees a blocked file.
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

var sw = System.Diagnostics.Stopwatch.StartNew();
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

        var targets = new List<RecycleTarget> { new(files[0], files[0]), new(files[0], files[0]) };

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

    private static IReadOnlyList<RecycleTarget> Targets(IReadOnlyList<string> files)
    {
        var targets = new List<RecycleTarget>(files.Count);

        foreach (var file in files)
        {
            targets.Add(new RecycleTarget(file, file));
        }

        return targets;
    }
}
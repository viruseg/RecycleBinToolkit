using System.Diagnostics;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class WorkerSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("WorkerSuite." + nameof(SequentialSubmitsAllSucceed), SequentialSubmitsAllSucceed),
        new TestCase("WorkerSuite." + nameof(ConcurrentSubmitsAllSucceed), ConcurrentSubmitsAllSucceed),
        new TestCase("WorkerSuite." + nameof(CallFromStaCallerDoesNotDeadlock), CallFromStaCallerDoesNotDeadlock),
        new TestCase("WorkerSuite." + nameof(CancellationReachesTheRetryLoop), CancellationReachesTheRetryLoop),
    ];

    /// <summary>
    /// Cancelling the wait must also reach the retry loop the worker is running. A batch that can never
    /// succeed on its own — a file held open, with an attempt budget worth seconds of backoff — is the
    /// case that tells the two apart. If the loop never sees the token it walks the whole budget on the
    /// one worker thread every other caller in the process queues behind, so a caller arriving straight
    /// after the cancellation is made to wait for work nobody is waiting for any more.
    /// </summary>
    /// <remarks>
    /// What is measured is the wait of the second caller, not the elapsed time of the cancelled one: the
    /// cancelled caller is released the moment its wait ends either way, so its own timing says nothing
    /// about what the worker went on to do.
    /// </remarks>
    private static async Task CancellationReachesTheRetryLoop(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var locked = workspace.CreateRandomFile(4096);
        var second = workspace.CreateRandomFile(4096);

        // Twenty rounds of a quarter-second of backoff is about five seconds of worker time the cancelled
        // batch would spend if its token never reached the loop.
        var options = new RecycleBinOptions
        {
            MaxAttempts = 20,
            InitialRetryDelay = TimeSpan.FromMilliseconds(250),
            OverallTimeout = TimeSpan.FromSeconds(30)
        };

        var stopwatch = Stopwatch.StartNew();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
        {
            var cancelled = RecycleBinWorker.SubmitAsync(Targets([locked]), options, cts.Token);

            await cts.CancelAsync();

            await Check.ThrowsAsync<OperationCanceledException>(
                () => cancelled,
                "the cancelled caller is told so rather than waiting out the retry budget");

            var after = await RecycleBinWorker
                .SubmitAsync(Targets([second]), RecycleBinOptions.Default, context.CancellationToken)
                .ConfigureAwait(false);

            var waited = stopwatch.Elapsed;

            Check.True(after.Succeeded, "the caller behind the cancelled one still gets its work done");
            Check.False(File.Exists(second), "and its file was really recycled");

            Check.True(
                waited < TimeSpan.FromSeconds(2),
                $"the cancelled batch did not keep the worker busy for the rest of its retry budget (waited {waited.TotalSeconds:0.0}s)");
        }

        workspace.Dispose();
    }

    private static Task SequentialSubmitsAllSucceed(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var observedThreadIds = new HashSet<int>();
        var recycled = new List<string>();

        for (var batch = 0; batch < 3; batch++)
        {
            var files = workspace.CreateRandomFiles(5, 2048);
            var targets = Targets(files);

            var result = RecycleBinWorker.SubmitAsync(targets, RecycleBinOptions.Default, context.CancellationToken)
                .GetAwaiter().GetResult();

            Check.True(result.Succeeded, $"batch {batch} succeeded (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
            Check.Equal(files.Count, result.RecycledPaths.Count, $"batch {batch} recycled every file");
            Check.Equal(0, result.Failures.Count, $"batch {batch} recorded no failures");

            foreach (var file in files)
            {
                Check.False(File.Exists(file), $"{file} is gone from disk");
            }

            recycled.AddRange(files);
            observedThreadIds.Add(RecycleBinWorker.ThreadId);
        }

        Check.Equal(1, observedThreadIds.Count, "every batch ran on the same worker thread");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ConcurrentSubmitsAllSucceed(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        const int submitters = 8;
        const int perSubmitter = 3;

        var batches = new IReadOnlyList<RecycleTarget>[submitters];
        var allFiles = new List<string>();

        for (var i = 0; i < submitters; i++)
        {
            var files = workspace.CreateRandomFiles(perSubmitter, 2048);
            batches[i] = Targets(files);
            allFiles.AddRange(files);
        }

        var tasks = batches.Select(targets => Task.Run(
            () => RecycleBinWorker.SubmitAsync(targets, RecycleBinOptions.Default, context.CancellationToken),
            context.CancellationToken)).ToArray();

        Task.WhenAll(tasks).GetAwaiter().GetResult();

        var results = tasks.Select(t => t.Result).ToArray();

        foreach (var result in results)
        {
            Check.True(result.Succeeded, $"a concurrent batch succeeded (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        }

        Check.Equal(allFiles.Count, results.Sum(r => r.RecycledPaths.Count), "no file was lost or double-counted across concurrent submits");
        Check.Equal(0, results.Sum(r => r.Failures.Count), "concurrent submits recorded no failures");

        foreach (var file in allFiles)
        {
            Check.False(File.Exists(file), $"{file} is gone from disk");
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task CallFromStaCallerDoesNotDeadlock(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(3, 2048);
        var targets = Targets(files);

        var staFailure = new TaskCompletionSource<RecycleBinResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var staThread = new Thread(() =>
        {
            try
            {
                staFailure.SetResult(
                    RecycleBinWorker.SubmitAsync(targets, RecycleBinOptions.Default, context.CancellationToken)
                        .GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                staFailure.SetException(ex);
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();

        if (!staFailure.Task.Wait(TimeSpan.FromSeconds(60)))
        {
            staThread.Interrupt();
            throw new TimeoutException("a submit from an STA caller never completed, which means the worker deadlocked.");
        }

        var result = staFailure.Task.GetAwaiter().GetResult();
        Check.True(result.Succeeded, $"an STA caller gets a clean result (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(files.Count, result.RecycledPaths.Count, "every file was recycled for the STA caller");

        foreach (var file in files)
        {
            Check.False(File.Exists(file), $"{file} is gone from disk");
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

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
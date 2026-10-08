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
        new TestCase("WorkerSuite." + nameof(ACancelledWaitStillLetsTheWorkerFinishItsWork), ACancelledWaitStillLetsTheWorkerFinishItsWork),
    ];

    /// <summary>
    /// A cancelled wait releases the caller and nothing else: the work handed over is performed anyway,
    /// and the caller never learns its outcome. That is the documented contract — cancelling stops the
    /// wait, not the work — and it is what makes a fault on that work land somewhere nobody is reading.
    /// </summary>
    /// <remarks>
    /// Two gates, because the order of the two events is the whole point: the caller has to be released
    /// while the work is still blocked, and the work has to finish afterwards. If a caller that stopped
    /// waiting were somehow able to swallow the work, the batch would quietly vanish, which is the worse
    /// of the two failures and the one a reader of the documentation cannot rule out.
    /// <para>
    /// What cannot be pinned from here is whether that fault reaches the caller as an unobserved task
    /// exception: .NET raises it from a finalizer, and forcing one in this process hangs rather than
    /// reports. The worker reads the exception itself instead, which is what stops it.
    /// </para>
    /// </remarks>
    private static async Task ACancelledWaitStillLetsTheWorkerFinishItsWork(TestContext context)
    {
        var release = new ManualResetEventSlim(false);
        var workFinished = new ManualResetEventSlim(false);

        using var cts = new CancellationTokenSource();

        var abandoned = RecycleBinWorker.RunAsync<int>(
            () =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                workFinished.Set();
                return 42;
            },
            cts.Token);

        cts.Cancel();

        await Check
            .ThrowsAsync<OperationCanceledException>(
                () => abandoned,
                "the cancelled caller is released, rather than being made to wait for the work it handed over")
            .ConfigureAwait(false);

        Check.False(
            workFinished.Wait(TimeSpan.FromMilliseconds(200)),
            "and it was released while the work was still blocked, not after it");

        release.Set();

        Check.True(
            workFinished.Wait(TimeSpan.FromSeconds(30)),
            "the worker still performs the work a cancelled caller handed over");
    }

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

        // The worker is brought up before anything is measured, and before the locked batch is
        // submitted. Bringing it up here rather than just above the clock is the whole point: the worker
        // is one thread with one FIFO queue, so anything enqueued while the locked batch is still running
        // waits for that batch to finish. A barrier placed there would have drained the very batch this
        // case is about and then timed a second caller against an idle worker, which no regression in the
        // retry loop could fail.
        RecycleBinWorker.RunAsync(() => 0, context.CancellationToken).GetAwaiter().GetResult();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
        {
            var cancelled = RecycleBinWorker.SubmitAsync(Targets([locked]), options, cts.Token);

            await cts.CancelAsync();

            await Check.ThrowsAsync<OperationCanceledException>(
                () => cancelled,
                "the cancelled caller is told so rather than waiting out the retry budget");

            // Started here, so what it covers is the wait this caller actually makes: its own submit,
            // queued behind a batch whose token has just been cancelled.
            var stopwatch = Stopwatch.StartNew();


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

        Check.Equal(
            1,
            observedThreadIds.Distinct().Count(),
            "the worker names one thread, and it is the same one before and after every batch");

        // A constant agrees with itself. RecycleBinWorker.ThreadId is assigned once when the worker is
        // built and never changes, so reading it three times proves nothing about where the batches went.
        // What can be asked instead is the only question that has an answer to be wrong about: the worker
        // is asked to run something that reports its own thread, and that is compared against the id it
        // publishes.
        var named = RecycleBinWorker.ThreadId;

        Check.NotEqual(0, named, "the worker exists once something has been submitted to it");
        Check.NotEqual(Thread.CurrentThread.ManagedThreadId, named, "and it is not the thread that submitted the batches");

        var observedInsideTheWorker = RecycleBinWorker
            .RunAsync(() => Thread.CurrentThread.ManagedThreadId, context.CancellationToken)
            .GetAwaiter()
            .GetResult();

        Check.Equal(
            named,
            observedInsideTheWorker,
            "so work handed to the worker runs on the thread the worker says it is");

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
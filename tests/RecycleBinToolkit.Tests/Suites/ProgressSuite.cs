using System.Diagnostics;
using System.Runtime.Versioning;
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Covers progress reporting: that every item is reported once, that the operation does not wait for the
/// handler, and that a handler which throws reaches the failure handler rather than the operation.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal static class ProgressSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ProgressSuite." + nameof(AnEmptySinkReportsNothingAndCompletes), AnEmptySinkReportsNothingAndCompletes),
        new TestCase("ProgressSuite." + nameof(TwoSinksKeepTheirOwnCounters), TwoSinksKeepTheirOwnCounters),
        new TestCase("ProgressSuite." + nameof(EveryInputPathReportsExactlyOneRecycleEvent), EveryInputPathReportsExactlyOneRecycleEvent),
        new TestCase("ProgressSuite." + nameof(ARepeatedPathOnARefusingVolumeStillReports), ARepeatedPathOnARefusingVolumeStillReports),
        new TestCase("ProgressSuite." + nameof(ASlowHandlerDoesNotHoldUpTheOperation), ASlowHandlerDoesNotHoldUpTheOperation),
        new TestCase("ProgressSuite." + nameof(WaitingDeliversEverythingBeforeTheCallReturns), WaitingDeliversEverythingBeforeTheCallReturns),
        new TestCase("ProgressSuite." + nameof(AThrowingHandlerReachesTheFailureCallback), AThrowingHandlerReachesTheFailureCallback),
        new TestCase("ProgressSuite." + nameof(TheHandlerDoesNotRunOnTheCallersThread), TheHandlerDoesNotRunOnTheCallersThread),
        new TestCase("ProgressSuite." + nameof(ElapsedIsReadWhenTheEventIsCreated), ElapsedIsReadWhenTheEventIsCreated),
        new TestCase("ProgressSuite." + nameof(AnEmptyBatchReportsNothingAndDoesNotHang), AnEmptyBatchReportsNothingAndDoesNotHang),
        new TestCase("ProgressSuite." + nameof(OneOptionsInstanceServesTwoCalls), OneOptionsInstanceServesTwoCalls),
        new TestCase("ProgressSuite." + nameof(ACancelledCallStillFinishesItsQueue), ACancelledCallStillFinishesItsQueue),
        new TestCase("ProgressSuite." + nameof(EveryRestoredEntryReportsOnce), EveryRestoredEntryReportsOnce),
        new TestCase("ProgressSuite." + nameof(AMergesPutBackIsNotReported), AMergesPutBackIsNotReported),
        new TestCase("ProgressSuite." + nameof(AnEmptyReportsOneEventPerRecord), AnEmptyReportsOneEventPerRecord),
        new TestCase("ProgressSuite." + nameof(AnEmptyDoesNotCountWhenNobodyIsListening), AnEmptyDoesNotCountWhenNobodyIsListening),
        new TestCase("ProgressSuite." + nameof(AnUnresolvableRootStillFinishesItsQueue), AnUnresolvableRootStillFinishesItsQueue),
    ];

    /// <summary>
    /// A caller who asks for no reports must get no thread. The sink exists as soon as the property is
    /// assigned, so the path where nothing is ever written to it has to end rather than wait for a reader
    /// that was never started.
    /// </summary>
    private static async Task AnEmptySinkReportsNothingAndCompletes(TestContext context)
    {
        var sink = new RecycleProgressSink(null);

        await sink.CompleteAsync(wait: true).ConfigureAwait(false);

        Check.Equal(0, sink.NextIndex() - 1, "no report was handed out");
    }

    /// <summary>
    /// Two operations reporting at once must not share a counter, or an item numbered 3 in one call would
    /// arrive as the same item numbered 3 in the other.
    /// </summary>
    private static async Task TwoSinksKeepTheirOwnCounters(TestContext context)
    {
        var first = new List<int>();
        var second = new List<int>();

        var left = new RecycleProgressSink(progress => first.Add(progress.Index));
        var right = new RecycleProgressSink(progress => second.Add(progress.Index));

        left.SetCount(4);
        right.SetCount(4);

        for (var i = 0; i < 4; i++)
        {
            Report(left, $@"X:\left-{i}.bin");
            Report(right, $@"X:\right-{i}.bin");
        }

        await left.CompleteAsync(wait: true).ConfigureAwait(false);
        await right.CompleteAsync(wait: true).ConfigureAwait(false);

        Check.Equal("1, 2, 3, 4", string.Join(", ", first), "one call numbers its own items from 1");
        Check.Equal("1, 2, 3, 4", string.Join(", ", second), "and so does the other, separately");
    }

    /// <summary>
    /// The count a caller can divide by is the number of paths they handed over, so a path that was refused
    /// or repeated still has to arrive. A progress display frozen at half is the failure this witnesses.
    /// </summary>
    private static async Task EveryInputPathReportsExactlyOneRecycleEvent(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(2, 4096);
        var missing = Path.Combine(ScratchWorkspace.Root, "never-written.bin");

        var reported = new List<RecycleProgress>();
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var result = await RecycleBin
            .RecycleAsync([files[0], missing, files[1], files[0]], options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(
            result.Succeeded || result.Failures.Count == 1,
            $"only the missing path fails (got {result.Failures.Count}: {string.Join(", ", result.Failures.Select(f => f.Stage))})");

        var events = Snapshot(reported);
        Check.Equal(4, events.Count, "one report per path handed over, the repeated one included");

        foreach (var progress in events)
        {
            Check.Equal(4, progress.Count, $"the total is the batch ({progress.Index})");
        }

        Check.Equal("1, 2, 3, 4", string.Join(", ", events.Select(e => e.Index).Order()), "numbered once each");

        Check.Equal(RecycleStatus.Refused, StatusOf(events, missing, RecycleStatus.Refused), "a path that named nothing is refused, not failed");
        Check.NotNull(FailureOf(events, missing, RecycleStatus.Refused), "and carries the failure saying so");
        Check.Equal(RecycleFailureStage.PathValidation, FailureOf(events, missing, RecycleStatus.Refused)!.Stage, "which is that nothing was there");

        foreach (var file in files)
        {
            Check.Equal(
                RecycleStatus.Recycled,
                StatusOf(events, file, RecycleStatus.Recycled),
                $"a good path was recycled ({file})");
        }

        // The repeat is reported as well as acted on: two paths went in under one name, so the caller is
        // told about the one that was refused as well as the one that moved.
        Check.Equal(
            RecycleStatus.Refused,
            StatusOf(events, files[0], RecycleStatus.Refused),
            "and the path listed twice carries a second report saying the repeat was refused");
        Check.True(FailureOf(events, files[0], RecycleStatus.Refused) is null, "with nothing invented as a reason");

        workspace.Dispose();
    }

    /// <summary>
    /// A repeated path on a volume that cannot recycle it is dropped by the volume gate before the engine's
    /// own de-duplication ever sees it, so hooking only the second drop leaves one report for two paths.
    /// </summary>
    private static async Task ARepeatedPathOnARefusingVolumeStillReports(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume whose gate refuses before the engine is reached");

        var file = workspace.CreateRandomFile(4096);

        var reported = new List<RecycleProgress>();
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        await RecycleBin
            .RecycleAsync([file, file], options, context.CancellationToken)
            .ConfigureAwait(false);

        var events = Snapshot(reported);

        Check.Equal(2, events.Count, "two paths in, two reports out, even though the gate dropped the second");
        Check.True(
            events.All(progress => progress.Status == RecycleStatus.Refused),
            $"and both are refusals (got {string.Join(", ", events.Select(e => e.Status))})");

        workspace.Dispose();
    }

    /// <summary>
    /// The whole reason the handler is on a thread of its own: a caller whose handler takes a second per
    /// item must not make the operation take a second per item too.
    /// </summary>
    private static async Task ASlowHandlerDoesNotHoldUpTheOperation(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(4, 4096);

        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var options = new RecycleOptions
        {
            OnItemProcessed = _ =>
            {
                entered.Set();
                gate.Wait();
            },
        };

        try
        {
            var call = RecycleBin.RecycleAsync(files, options, context.CancellationToken);

            // The handler is parked inside itself. Everything after this asserts that the operation finished
            // the whole batch anyway, which is the claim and not a timing coincidence.
            Check.True(entered.Wait(TimeSpan.FromSeconds(30)), "the handler was reached");

            var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);

            Check.True(ReferenceEquals(finished, call), "the operation finished while its handler was still blocked");

            gate.Set();

            var result = await call.ConfigureAwait(false);
            Check.True(result.Succeeded, $"and every file really went (first failure: {result.FirstFailure?.Stage})");
        }
        finally
        {
            gate.Set();
        }

        workspace.Dispose();
    }

    /// <summary>
    /// Waiting is the one thing that puts the caller's handler on the caller's time, and it has to deliver
    /// everything it promised before it returns.
    /// </summary>
    private static async Task WaitingDeliversEverythingBeforeTheCallReturns(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(5, 4096);

        var reported = new List<RecycleProgress>();
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var result = await RecycleBin.RecycleAsync(files, options, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the batch went (first failure: {result.FirstFailure?.Stage})");

        var events = Snapshot(reported);
        Check.Equal(files.Count, events.Count, "every report was delivered before the call returned, with no wait of any kind");

        workspace.Dispose();
    }

    /// <summary>
    /// A handler that throws is the caller's bug, not the operation's: the operation has already decided
    /// everything it reports, so the fault belongs to a second handler rather than to the call's result.
    /// </summary>
    private static async Task AThrowingHandlerReachesTheFailureCallback(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(4, 4096);

        var calls = 0;
        Exception? faulted = null;

        var options = new RecycleOptions
        {
            OnItemProcessed = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("the caller's own fault");
            },
            OnProgressFailed = ex => faulted = ex,
            WaitForProgressCallbacks = true,
        };

        var result = await RecycleBin.RecycleAsync(files, options, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the operation carried on regardless (first failure: {result.FirstFailure?.Stage})");
        Check.Equal(files.Count, result.RecycledPaths.Count, "and every file really went");
        Check.NotNull(faulted, "the failure handler was told");
        Check.Equal("the caller's own fault", faulted!.Message, "with the exception it was given");
        Check.Equal(1, Volatile.Read(ref calls), "and the handler was not called again after it threw");

        workspace.Dispose();
    }

    /// <summary>
    /// A caller whose handler touches a control, or a list another thread is reading, needs the reports one
    /// at a time rather than concurrently — and needs to know they are not arriving on the thread that
    /// called the operation, which a caller on a UI thread gave back at the first await.
    /// </summary>
    private static async Task TheHandlerDoesNotRunOnTheCallersThread(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(6, 4096);

        var callers = Environment.CurrentManagedThreadId;
        var inFlight = 0;
        var overlapped = 0;

        var options = new RecycleOptions
        {
            OnItemProcessed = _ =>
            {
                if (Interlocked.Increment(ref inFlight) != 1)
                {
                    Interlocked.Increment(ref overlapped);
                }

                if (Environment.CurrentManagedThreadId == callers)
                {
                    Interlocked.Increment(ref overlapped);
                }

                Thread.Sleep(20);
                Interlocked.Decrement(ref inFlight);
            },
            WaitForProgressCallbacks = true,
        };

        await RecycleBin.RecycleAsync(files, options, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(
            0,
            Volatile.Read(ref overlapped),
            "no report was ever delivered on top of another, and none on the thread that called the operation");

        workspace.Dispose();
    }

    /// <summary>
    /// Elapsed answers how far the operation had got, so a caller can tell a slow disk from a slow handler.
    /// Stamped at delivery instead of at creation, a handler that blocks would push every later report's
    /// time forward and the two would be indistinguishable.
    /// </summary>
    private static async Task ElapsedIsReadWhenTheEventIsCreated(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(3, 4096);

        using var firstSeen = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var reported = new List<RecycleProgress>();
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }

                if (progress.Index == 1)
                {
                    firstSeen.Set();
                    gate.Wait();
                }
            },
            WaitForProgressCallbacks = true,
        };

        TimeSpan heldFor;
        var started = Stopwatch.StartNew();

        try
        {
            var call = RecycleBin.RecycleAsync(files, options, context.CancellationToken);

            Check.True(firstSeen.Wait(TimeSpan.FromSeconds(30)), "the first report was reached");

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            heldFor = started.Elapsed;
            gate.Set();

            await call.ConfigureAwait(false);
        }
        finally
        {
            gate.Set();
        }

        var events = Snapshot(reported);
        Check.Equal(files.Count, events.Count, "every report arrived");

        // Creation of report two happened while the handler was parked on report one, so its stamped time
        // is a fraction of the hold. Delivery would have put it at or past the end of the hold.
        Check.True(
            events[1].Elapsed < heldFor / 2,
            $"a report made while the handler was parked is stamped at its creation, not its delivery (stamped {events[1].Elapsed}, held for {heldFor})");

        workspace.Dispose();
    }

    /// <summary>
    /// An empty batch is a batch of nothing, not a batch that never finished: the caller's handler is set,
    /// the call is told to wait, and there is nothing to wait for.
    /// </summary>
    private static async Task AnEmptyBatchReportsNothingAndDoesNotHang(TestContext context)
    {
        var reported = new List<RecycleProgress>();
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var call = RecycleBin.RecycleAsync([], options, context.CancellationToken);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        Check.True(ReferenceEquals(finished, call), "an empty batch with a handler set and waiting still returns");
        Check.True((await call.ConfigureAwait(false)).Succeeded, "and answers for itself");
        Check.Equal(0, Snapshot(reported).Count, "nothing is reported for a batch of nothing");
    }

    /// <summary>
    /// An options object a caller keeps and hands to a second call must still report. The queue the first
    /// call completed is not the queue the second call writes to, or the handler would simply stop being
    /// called with nothing said about it.
    /// </summary>
    private static async Task OneOptionsInstanceServesTwoCalls(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batches to recycle");

        var reported = new List<RecycleProgress>();
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var first = workspace.CreateRandomFiles(2, 4096);
        var second = workspace.CreateRandomFiles(2, 4096);

        var firstResult = await RecycleBin.RecycleAsync(first, options, context.CancellationToken).ConfigureAwait(false);
        var secondResult = await RecycleBin.RecycleAsync(second, options, context.CancellationToken).ConfigureAwait(false);

        Check.True(firstResult.Succeeded, "the first call recycled its files");
        Check.True(secondResult.Succeeded, "and so did the second");

        var events = Snapshot(reported);
        Check.Equal(4, events.Count, "the second call reported as well as the first, over the same options instance");

        workspace.Dispose();
    }

    /// <summary>
    /// A cancelled operation raises, and raising is how the caller learns the operation is over. It must
    /// still close the queue on the way out, or a caller that asked to wait would wait for a reader that is
    /// never told to stop.
    /// </summary>
    private static async Task ACancelledCallStillFinishesItsQueue(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the batch to recycle");

        var files = workspace.CreateRandomFiles(200, 4096);

        using var source = new CancellationTokenSource();
        var reported = 0;

        var options = new RecycleOptions
        {
            // Cancelling from the handler rather than on a timer is what makes this land inside the batch:
            // one report is out of two hundred, and the engine checks the token before every path.
            OnItemProcessed = _ =>
            {
                Interlocked.Increment(ref reported);
                source.Cancel();
            },
            WaitForProgressCallbacks = true,
        };

        var call = RecycleBin.RecycleAsync(files, options, source.Token);

        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);

        Check.True(ReferenceEquals(finished, call), "the call returns rather than waiting on a reader nobody would close");

        await Check
            .ThrowsAsync<OperationCanceledException>(() => call, "cancelling mid-batch raises rather than answering")
            .ConfigureAwait(false);

        Check.True(Volatile.Read(ref reported) < files.Count, $"and the batch really was abandoned (got {Volatile.Read(ref reported)} of {files.Count})");

        workspace.Dispose();
    }

    /// <summary>
    /// Every entry a caller handed over is reported once, and every report carries the entry's own number
    /// from one counter the engine's parallel volumes share.
    /// </summary>
    private static async Task EveryRestoredEntryReportsOnce(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries to restore");

        var volumeRoot = ScratchWorkspace.VolumeRoot;

        var files = workspace.CreateRandomFiles(6, 4096);
        var recycled = await RecycleBin.RecycleAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(recycled.Succeeded, $"the files reached the bin (first failure: {recycled.FirstFailure?.Stage})");

        var entries = Listing
            .OnlyEntries(
                await RecycleBin.GetItemsAsync(volumeRoot, new GetItemsOptions { PreloadItemDetails = true }, context.CancellationToken)
                    .ConfigureAwait(false),
                volumeRoot)
            .Where(item => files.Any(file => string.Equals(file, item.OriginalPath, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Check.Equal(files.Count, entries.Length, "every one of them came back out of the listing");

        var reported = new List<RestoreProgress>();
        var options = new RestoreOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var result = await RecycleBin.RestoreAsync(entries, options, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(files.Count, result.ItemsRestored, $"every entry went back (failures: {string.Join(", ", result.Failures.Select(f => f.Stage))})");

        var events = Snapshot(reported);

        Check.Equal(files.Count, events.Count, "one report per entry handed over, across whatever volumes ran at once");
        Check.Equal(
            string.Join(", ", Enumerable.Range(1, files.Count)),
            string.Join(", ", events.Select(e => e.Index).Order()),
            "numbered from one shared counter, each number handed out once");
        Check.True(events.All(progress => progress.Count == files.Count), "against the number of entries asked about");
        Check.True(events.All(progress => progress.Status == RestoreStatus.Restored), "and every one of them restored");

        workspace.Dispose();
    }

    /// <summary>
    /// A merge that could not put a file back sends it to the Recycle Bin again. Those files were never in
    /// the caller's list, so reporting them would push the reports' own index past the count the caller is
    /// holding — for items they never asked about.
    /// </summary>
    private static async Task AMergesPutBackIsNotReported(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("a directory to merge into one already on disk");

        var tree = Path.Combine(ScratchWorkspace.Root, "tree");
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "collides.bin"), "incoming", context.CancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(tree, "free.bin"), "incoming too", context.CancellationToken)
            .ConfigureAwait(false);

        var recycled = await RecycleBin
            .RecycleAsync([tree], new RecycleOptions { ThrowOnError = false }, context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(recycled.Succeeded, $"the directory reached the bin (first failure: {recycled.FirstFailure?.Stage})");

        // Put back at the path the directory was deleted from, holding one of the files it is about to be
        // merged over. That file is the one the merge cannot place, which is what sends it to the bin again.
        Directory.CreateDirectory(tree);
        await File.WriteAllTextAsync(Path.Combine(tree, "collides.bin"), "already here", context.CancellationToken)
            .ConfigureAwait(false);

        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var entry = Listing
            .OnlyEntries(
                await RecycleBin.GetItemsAsync(volumeRoot, new GetItemsOptions { PreloadItemDetails = true }, context.CancellationToken)
                    .ConfigureAwait(false),
                volumeRoot)
            .FirstOrDefault(item => item.IsDirectory && string.Equals(item.OriginalPath, tree, StringComparison.OrdinalIgnoreCase));

        Check.NotNull(entry, "the directory came back out of the listing");

        var reported = new List<RestoreProgress>();
        var options = new RestoreOptions
        {
            DirectoryConflict = RestoreDirectoryConflict.MergeSkip,
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var restored = await RecycleBin.RestoreAsync([entry!], options, context.CancellationToken).ConfigureAwait(false);

        // Without this the case would pass on a merge that placed everything, and there would be no put-back
        // for the assertion below to be about.
        Check.True(
            restored.Conflicts.Any(conflict => conflict.Action == RecycleRestoreConflictAction.MergedSkipped),
            $"a file the merge could not place went back to the bin ({string.Join(", ", restored.Conflicts.Select(c => c.Action))})");

        var events = Snapshot(reported);

        Check.Equal(1, events.Count, "one entry in, one report out, however many of its files went back to the bin");
        Check.Equal(RestoreStatus.Merged, events[0].Status, "and it is reported as merged rather than restored");
        Check.Equal(1, events[0].Count, "against a count of the entries handed over, not of the files involved");

        workspace.Dispose();
    }

    /// <summary>
    /// An empty knows nothing about what it is going to remove until it has read the bin, so the total a
    /// progress display divides by costs one walk of every volume first. Every entry it then goes after is
    /// reported, against that total.
    /// </summary>
    private static async Task AnEmptyReportsOneEventPerRecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the empty to remove");

        var files = workspace.CreateRandomFiles(5, 4096);
        var recycled = await RecycleBin.RecycleAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(recycled.Succeeded, $"the files reached the bin (first failure: {recycled.FirstFailure?.Stage})");

        var reported = new List<EmptyProgress>();
        var options = new EmptyOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var emptied = await RecycleBin
            .EmptyAsync(ScratchWorkspace.VolumeRoot, options, context.CancellationToken)
            .ConfigureAwait(false);

        var events = Snapshot(reported);

        // Measured against the empty's own figures and not against this case's five files: the volume's
        // bin holds whatever else the run left there, and the contract is about the entries the empty went
        // after rather than about this case.
        Check.Equal(
            emptied.ItemsRemoved,
            events.Count(progress => progress.Status == EmptyStatus.Removed),
            $"one report per entry that left the bin (removed {emptied.ItemsRemoved})");
        Check.Equal(
            emptied.Failures.Count,
            events.Count(progress => progress.Status == EmptyStatus.Failed),
            $"and one per entry that did not ({string.Join(", ", emptied.Failures.Select(f => f.Stage))})");
        Check.Equal(emptied.ItemsRemoved + emptied.Failures.Count, events.Count, "and no other report at all");

        var swept = events.Count;
        Check.True(events.All(progress => progress.Count == swept), "every report carries the total counted before the sweep");
        Check.Equal(
            string.Join(", ", Enumerable.Range(1, swept)),
            string.Join(", ", events.Select(progress => progress.Index).Order()),
            "numbered once each");

        workspace.Dispose();
    }

    /// <summary>
    /// The count is a second walk of every bin directory, and a caller who asked for no reports must not
    /// pay for it: on a bin of thirteen thousand entries that walk is not a rounding error.
    /// </summary>
    private static async Task AnEmptyDoesNotCountWhenNobodyIsListening(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries for the empty to remove");

        var files = workspace.CreateRandomFiles(3, 4096);
        await RecycleBin.RecycleAsync(files, cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var before = NativeBinEmptier.CountPasses;

        await RecycleBin
            .EmptyAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(before, NativeBinEmptier.CountPasses, "an empty nobody is listening to counts nothing");

        workspace.Dispose();
    }

    /// <summary>
    /// A root that names no volume is answered for without the sweep, and the answer still has to close the
    /// queue: a caller who asked to wait would otherwise wait on a reader nobody ever tells to stop.
    /// </summary>
    private static async Task AnUnresolvableRootStillFinishesItsQueue(TestContext context)
    {
        var reported = new List<EmptyProgress>();
        var options = new EmptyOptions
        {
            OnItemProcessed = progress =>
            {
                lock (reported)
                {
                    reported.Add(progress);
                }
            },
            WaitForProgressCallbacks = true,
        };

        var before = NativeBinEmptier.CountPasses;

        var call = RecycleBin.EmptyAsync(string.Empty, options, context.CancellationToken);
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);

        Check.True(ReferenceEquals(finished, call), "the call returns rather than waiting on a reader nobody would close");

        var result = await call.ConfigureAwait(false);

        Check.Equal(1, result.Failures.Count, "and answers that nothing was a volume root");
        Check.Equal(before, NativeBinEmptier.CountPasses, "without reading anything to count");
        Check.Equal(0, Snapshot(reported).Count, "nothing is reported for a sweep that never happened");
    }

    private static void Report(ProgressSink sink, string path)
    {
        sink.Report(new RecycleProgress(
            sink.NextIndex(),
            sink.Count,
            RecycleStatus.Recycled,
            path,
            sink.Elapsed,
            Failure: null));
    }

    private static List<T> Snapshot<T>(List<T> source)
    {
        lock (source)
        {
            return [.. source];
        }
    }

private static RecycleStatus StatusOf(IReadOnlyList<RecycleProgress> events, string path, RecycleStatus wanted) =>
        events
            .Where(progress => string.Equals(progress.Path, path, StringComparison.OrdinalIgnoreCase))
            .Select(progress => progress.Status)
            .FirstOrDefault(status => status == wanted);

private static RecycleFailure? FailureOf(IReadOnlyList<RecycleProgress> events, string path, RecycleStatus wanted) =>
        events.First(progress =>
            string.Equals(progress.Path, path, StringComparison.OrdinalIgnoreCase) && progress.Status == wanted).Failure;
}
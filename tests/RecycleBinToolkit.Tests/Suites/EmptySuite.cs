using System.Diagnostics;
using System.Runtime.Versioning;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Covers <see cref="RecycleBin.EmptyAsync"/>: that the entries really leave the Recycle Bin, and
/// that the counts it reports are measured rather than assumed.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class EmptySuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("EmptySuite." + nameof(EmptyRemovesWhatWasJustRecycled), EmptyRemovesWhatWasJustRecycled),
        new TestCase("EmptySuite." + nameof(EmptyReportsWhatItFreed), EmptyReportsWhatItFreed),
        new TestCase("EmptySuite." + nameof(EmptyIsSafeToRunOnAnAlreadyEmptyVolume), EmptyIsSafeToRunOnAnAlreadyEmptyVolume),
        new TestCase("EmptySuite." + nameof(EmptyOnAnAbsentVolumeIsReportedNotThrown), EmptyOnAnAbsentVolumeIsReportedNotThrown),
        new TestCase("EmptySuite." + nameof(ElapsedCoversTheWaitAsWellAsTheEmpty), ElapsedCoversTheWaitAsWellAsTheEmpty),
    ];

    /// <summary>
    /// Every operation queues behind one Shell worker, so time spent waiting for it is part of what the
    /// caller waited. Elapsed has to say so, and say the same thing as a listing's does.
    /// </summary>
    /// <remarks>
    /// The work runs on the worker thread, so a stopwatch started there reports only the Shell's share and
    /// omits the queueing entirely. That is the larger half whenever the worker is busy with another
    /// batch, and it is invisible to the caller precisely because it happened before their call reached
    /// the Shell.
    /// <para>
    /// The assertion is that the reported time is not smaller than the time the call itself took, which is
    /// the only direction that can be checked from outside — and the only one that matters: an elapsed
    /// time that understates the wait is worse than one that overstates it.
    /// </para>
    /// </remarks>
    private static async Task ElapsedCoversTheWaitAsWellAsTheEmpty(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(2, 4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        // Occupy the worker first, so the empty cannot start until this finishes. Without that the queueing
        // is indistinguishable from no queueing, and the assertion below would be satisfied by a worker-side
        // measurement too — which is the whole question.
        var blocking = RecycleBinWorker.RunAsync(
            () =>
            {
                Thread.Sleep(WorkerOccupation);
                return true;
            },
            context.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var emptied = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        await blocking.ConfigureAwait(false);

        Check.True(
            emptied.Elapsed >= WorkerOccupation - Slack,
            $"the reported time includes waiting for the worker, not just the empty (reported {emptied.Elapsed.TotalMilliseconds:F0} ms, worker was occupied for {WorkerOccupation.TotalMilliseconds:F0} ms)");

        Check.True(
            emptied.Elapsed <= stopwatch.Elapsed,
            $"and it still cannot exceed the time the call itself took (reported {emptied.Elapsed.TotalMilliseconds:F0} ms, call took {stopwatch.Elapsed.TotalMilliseconds:F0} ms)");
    }

    /// <summary>How long the worker is held before the empty is issued.</summary>
    private static readonly TimeSpan WorkerOccupation = TimeSpan.FromMilliseconds(1500);

    /// <summary>Slack for the sleep overshooting its own duration.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The claim a caller depends on: after emptying, the entry is gone from the Recycle Bin, not
    /// merely marked as handled. Listing it again is the only way to know.
    /// </summary>
    /// <remarks>
    /// Only the listing after the empty is a full one. Reading the bin beforehand to prove the file was
    /// in it costs a full enumeration, which is the most expensive thing this suite could do, and it
    /// proves something <see cref="ListerSuite"/> already proves far more cheaply: the recycle itself
    /// reported success, and a counter reading covers the same ground for the purposes of this test.
    /// </remarks>
    private static async Task EmptyRemovesWhatWasJustRecycled(TestContext context)
    {
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var (countBefore, _) = RecycleBinProbe.Measure(volumeRoot);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        Check.True(
            RecycleBinProbe.Measure(volumeRoot).Items - countBefore >= 1,
            "the file reached the Recycle Bin before emptying");

        var emptied = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        var after = await RecycleBin.GetItemsAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);
        Check.False(after.Items.Any(i => i.OriginalPath == files[0]), "the file is no longer in the Recycle Bin");
        Check.Equal(0, after.Items.Count, $"the volume holds no recycled item afterwards ({after.Items.Count} left)");
        Check.Equal(0, emptied.Failures.Count, $"emptying the volume reports no failure ({string.Join(", ", emptied.Failures.Select(f => f.Path + " 0x" + f.HResult.ToString("X8")))})");

        workspace.Dispose();
    }

    /// <summary>
    /// The counters are the reason the Shell API is wrapped rather than called: it removes items
    /// without saying how many. Both counts are checked against the size of what was deleted, because
    /// a result that reports zero while the caller watched the bytes disappear is worse than no
    /// result.
    /// </summary>
    private static async Task EmptyReportsWhatItFreed(TestContext context)
    {
        const int fileCount = 3;
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(fileCount, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the files were recycled");

        var emptied = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.True(emptied.ItemsRemoved >= fileCount, $"the removed item count covers the batch (got {emptied.ItemsRemoved}, expected at least {fileCount})");
        Check.True(emptied.BytesFreed >= fileCount * (long)fileSize, $"the freed byte count covers the batch (got {emptied.BytesFreed}, expected at least {fileCount * fileSize})");
        Check.True(emptied.VolumesEmptied.Contains(volumeRoot, StringComparer.OrdinalIgnoreCase), $"the emptied volume is named (got {string.Join(", ", emptied.VolumesEmptied)})");

        workspace.Dispose();
    }

    /// <summary>
    /// Emptying an empty volume is the case a scheduled cleanup hits on every run after the first. It
    /// must report success with nothing removed rather than fail, or a cleanup job would treat a clean
    /// Recycle Bin as an error.
    /// </summary>
    private static async Task EmptyIsSafeToRunOnAnAlreadyEmptyVolume(TestContext context)
    {
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        await FileRecycler.MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken).ConfigureAwait(false);
        await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        var second = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, second.Failures.Count, "emptying an empty volume is not a failure");
        Check.Equal(0, second.ItemsRemoved, "nothing was removed, so nothing is reported removed");
        Check.Equal(0, second.BytesFreed, "nothing was freed, so nothing is reported freed");

        workspace.Dispose();
    }

    /// <summary>
    /// A volume letter that is not mounted is an environmental condition, and a service emptying
    /// drives must keep going rather than take an exception.
    /// </summary>
    private static async Task EmptyOnAnAbsentVolumeIsReportedNotThrown(TestContext context)
    {
        var emptied = await RecycleBin.EmptyAsync("Z:\\", context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, emptied.ItemsRemoved, "an absent volume removes nothing");
        Check.Equal(0, emptied.BytesFreed, "an absent volume frees nothing");
        Check.True(emptied.Failures.Count > 0, $"the absent volume is reported rather than swallowed (got {emptied.Failures.Count} failure(s))");
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using RecycleBinToolkit;
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
        new TestCase("EmptySuite." + nameof(APathInsideAVolumeIsNotAVolumeToEmpty), APathInsideAVolumeIsNotAVolumeToEmpty),
        new TestCase("EmptySuite." + nameof(ABareDriveLetterEmptiesTheVolumeItNames), ABareDriveLetterEmptiesTheVolumeItNames),
        new TestCase("EmptySuite." + nameof(AnExtendedLengthRootIsTheVolumeToEmpty), AnExtendedLengthRootIsTheVolumeToEmpty),
        new TestCase("EmptySuite." + nameof(AnEmptyPathIsNotAVolumeToEmpty), AnEmptyPathIsNotAVolumeToEmpty),
        new TestCase("EmptySuite." + nameof(AForwardSlashSpellingIsTheVolumeToEmpty), AForwardSlashSpellingIsTheVolumeToEmpty),
        new TestCase("EmptySuite." + nameof(NamingOneVolumeEmptiesThatVolume), NamingOneVolumeEmptiesThatVolume),
    ];

    /// <summary>
    /// <c>SHEmptyRecycleBin</c> reads a null root as every volume on the machine. Naming one volume has to
    /// reach it as that volume, so a caller who mistyped a root cannot have every Recycle Bin emptied under
    /// a result that reports one volume.
    /// </summary>
    /// <remarks>
    /// The root handed to the Shell is not the caller's spelling, it is the normalised one the measurement
    /// and the report are both taken against. Asserting on the exact string the caller wrote would pass
    /// against the wrong spelling being handed over; asserting on the normalised root catches that too.
    /// <para>
    /// What this cannot decide, and why, is the one-root-versus-all-roots distinction itself. An empty
    /// Recycle Bin is emptied successfully, so a result naming one volume comes back identically whether
    /// the Shell was handed that volume or handed <c>null</c> — the check that would tell them apart needs
    /// a second volume with something in its bin, and arranging that means either writing into a volume
    /// the caller did not name or emptying one. So the decision is pinned as a pure function in
    /// <see cref="ContractSuite.TheShellIsOnlyEverAskedAboutVolumesThatWereMeasured"/>, where it can be,
    /// and this case covers what only the public API can reach: that a named volume is emptied and
    /// measured, after the decision moved.
    /// </para>
    /// </remarks>
    private static async Task NamingOneVolumeEmptiesThatVolume(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries in the volume's Recycle Bin to count");

        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;
        var files = workspace.CreateRandomFiles(2, 4096);

        // Recycled first, so the empty has something to remove. An already-empty bin reports a success
        // either way, which is what makes this case vacuous without them.
        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(recycled.Succeeded, $"the files reached the volume's Recycle Bin (first failure: {recycled.FirstFailure?.Stage} 0x{recycled.FirstFailure?.HResult:X8})");
        Check.Equal(files.Count, recycled.RecycledPaths.Count, "all of them did");

        var emptied = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(1, emptied.VolumesEmptied.Count, $"naming one volume covers one volume (got {string.Join(", ", emptied.VolumesEmptied)})");
        Check.Equal(volumeRoot, emptied.VolumesEmptied[0], "and it is that volume");
        Check.Equal(0, emptied.Failures.Count, "with nothing left unreached");
        Check.True(emptied.ItemsRemoved >= files.Count, $"and the entries really left it (got {emptied.ItemsRemoved})");
        Check.True(emptied.BytesFreed >= files.Count * 4096L, $"and their bytes were measured as freed (got {emptied.BytesFreed})");

        workspace.Dispose();
    }

    /// <summary>
    /// An empty string names no volume, so the empty has to refuse it. Reading it as a root instead is
    /// worse than a refusal: the caller is answered for the whole drive while believing they asked about
    /// nothing in particular.
    /// </summary>
    /// <remarks>
    /// The refusal has to happen before the Shell is reached. An empty string reduced to a root is
    /// <c>"\</c>, and handing that to <c>SHEmptyRecycleBin</c> is a question about a volume that does not
    /// exist — or, worse, about no volume at all. A refusal cannot do that, so the path is checked before
    /// the Shell is asked anything.
    /// <para>
    /// <see cref="RecycleBin.GetStatusAsync"/> throws on the same string rather than refusing it, because
    /// it takes no <see langword="null"/> to mean "everything"; that difference is its own and predates
    /// this case.
    /// </para>
    /// </remarks>
    private static async Task AnEmptyPathIsNotAVolumeToEmpty(TestContext context)
    {
        Check.False(
            RecycleBinVolumeState.IsVolumeRoot(string.Empty),
            "an empty string is not a volume root, however it is reduced");

        var result = await RecycleBin.EmptyAsync(string.Empty, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, result.VolumesEmptied.Count, "no volume is emptied in answer to an empty path");
        Check.Equal(0, result.ItemsRemoved, "and nothing is removed");
        Check.Equal(1, result.Failures.Count, "the caller is told the path was not one");

        var failure = result.Failures[0];
        Check.Equal(RecycleFailureStage.PathValidation, failure.Stage, "as a path that was not a volume root, before the Shell was asked");
        Check.Equal(string.Empty, failure.Path, "naming the path the caller passed");
    }

    /// <summary>
    /// The status and the empty both decide what a volume root is, so a root they disagree about splits
    /// one library into two answers for one string. A root spelled with a forward slash is where that
    /// happened, and it needs no network to show it.
    /// </summary>
    /// <remarks>
    /// <c>Path.GetPathRoot("W:/")</c> answers <c>W:\</c>, so comparing a caller's spelling against its
    /// root as typed calls that a path inside a volume — while a reduced comparison calls it the root,
    /// which is what it is. The two public methods used to reduce differently, and one of them refused
    /// this string while the other answered for <c>W:\</c>.
    /// <para>
    /// This is the guard for that duplication on a local drive. <c>\\?\W:\</c> is not: the Shell hands
    /// that spelling back as its own root, so both predicates read it as a root and
    /// <see cref="AnExtendedLengthRootIsTheVolumeToEmpty"/> cannot tell them apart. A share root can tell
    /// them apart, and is the later manual group's case to make, because the empty hands an accepted share
    /// to the Shell and the default run must not reach for the network.
    /// </para>
    /// </remarks>
    private static async Task AForwardSlashSpellingIsTheVolumeToEmpty(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("an entry in the bin for a forward-slash root to empty");

        var file = workspace.CreateRandomFile(4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;
        var forwardSlash = volumeRoot.Replace('\\', '/');

        var recycled = await FileRecycler.MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "there is something in the bin for the check to be about");

        var status = await RecycleBin.GetStatusAsync(forwardSlash, context.CancellationToken).ConfigureAwait(false);
        Check.Equal(volumeRoot, status.VolumeRoot, "the status reads a forward-slash root as the volume it is");

        var result = await RecycleBin.EmptyAsync(forwardSlash, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(
            0,
            result.Failures.Count,
            result.Failures.Count == 0
                ? "and the empty reads it the same way, instead of refusing it"
                : $"and the empty reads it the same way, instead of refusing it (first failure: {result.Failures[0].Stage} 0x{result.Failures[0].HResult:X8})");
        Check.Equal(1, result.VolumesEmptied.Count, "so one volume is emptied rather than none");
        Check.Equal(volumeRoot, result.VolumesEmptied[0], "and it is the volume the status named");
        Check.True(result.ItemsRemoved >= 1, $"and the entry really went (removed {result.ItemsRemoved})");

        workspace.Dispose();
    }

    /// <summary>
    /// A bare drive letter names its volume, and so does the same root written with its separator.
    /// Both spellings have to empty the same volume, and both have to say so in what they report —
    /// an entry that claims a volume was emptied, having asked the Shell for something else, is worse
    /// than a refusal because nothing looks wrong.
    /// </summary>
    /// <remarks>
    /// <c>Path.GetPathRoot("W:")</c> is <c>W:</c>, which is drive-relative rather than a root. It is a
    /// spelling callers get wrong often enough that <see cref="RecycleBin.GetStatusAsync"/> normalises
    /// it, and the empty has to agree: the volume measured, the volume emptied and the volume named in
    /// <see cref="RecycleBinEmptyResult.VolumesEmptied"/> must all be the same one.
    /// </remarks>
    private static async Task ABareDriveLetterEmptiesTheVolumeItNames(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("an entry in the bin for a bare drive letter to empty");

        var file = workspace.CreateRandomFile(4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await FileRecycler.MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "there is something in the bin for the check to be about");

        var bare = volumeRoot.TrimEnd(Path.DirectorySeparatorChar);
        var result = await RecycleBin.EmptyAsync(bare, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(
            0,
            result.Failures.Count,
            result.Failures.Count == 0
                ? "a bare drive letter is a volume root"
                : $"a bare drive letter is a volume root (first failure: {result.Failures[0].Stage} 0x{result.Failures[0].HResult:X8})");
        Check.Equal(1, result.VolumesEmptied.Count, "and one volume is reported as emptied");
        Check.Equal(volumeRoot, result.VolumesEmptied[0], "named with its separator, however the caller spelled it");
        Check.True(result.ItemsRemoved >= 1, $"and the entry really went (removed {result.ItemsRemoved})");

        workspace.Dispose();
    }

    /// <summary>
    /// The empty and the status both decide what a volume root is, and they have to decide it the same
    /// way: a caller asking both about one string is asking one question. Here they are asked about a root
    /// written with the extended-length prefix, and the empty has to act on the volume the status named
    /// rather than on the prefixed spelling of it.
    /// </summary>
    /// <remarks>
    /// The two used to carry a copy of that decision each, and the empty's copy compared the caller's
    /// spelling as typed — which is how a share root came back refused by one and answered by the other.
    /// <para>
    /// A share root is the spelling that first showed it, and it cannot be the one pinned here: once the
    /// empty accepts a share it goes on to query that share's bin and hand it an empty, and neither is a
    /// question to ask of a name that may turn out to be somebody's real share. What this case does pin is
    /// the prefix side of the same predicate — <c>Path.GetPathRoot</c> hands <c>\\?\W:\</c> back
    /// verbatim, so that root was never the one the two disagreed about, and its value is the agreement
    /// itself: both public methods reading a root as a root, and the empty reporting which volume it
    /// emptied.
    /// </para>
    /// </remarks>
    private static async Task AnExtendedLengthRootIsTheVolumeToEmpty(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("an entry in the bin for a prefixed root to empty");

        var file = workspace.CreateRandomFile(4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;
        var prefixed = @"\\?\" + volumeRoot;

        var recycled = await FileRecycler.MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "there is something in the bin for the check to be about");

        var status = await RecycleBin.GetStatusAsync(prefixed, context.CancellationToken).ConfigureAwait(false);
        Check.Equal(volumeRoot, status.VolumeRoot, "the status names the plain volume, so there is one volume to act on");

        var result = await RecycleBin.EmptyAsync(prefixed, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(
            0,
            result.Failures.Count,
            result.Failures.Count == 0
                ? "a root written with the extended-length prefix is a volume root to empty"
                : $"a root written with the extended-length prefix is a volume root to empty (first failure: {result.Failures[0].Stage} 0x{result.Failures[0].HResult:X8})");
        Check.Equal(1, result.VolumesEmptied.Count, "and one volume is reported as emptied");
        Check.Equal(volumeRoot, result.VolumesEmptied[0], "the same volume the status named, not the prefixed spelling");
        Check.True(result.ItemsRemoved >= 1, $"and the entry really went (removed {result.ItemsRemoved})");

        workspace.Dispose();
    }

    /// <summary>
    /// Emptying is the widest thing this library does, so it has to be pointed at the volume the caller
    /// named. A path inside a volume names no volume of its own, and clearing every recycled item on that
    /// whole volume in answer to it is not a smaller version of what was asked for.
    /// </summary>
    /// <remarks>
    /// <see cref="RecycleBin.GetStatusAsync"/> answers the same input with VolumeNotFound rather than
    /// reporting on the volume containing it, so the two agree on what a volume root is.
    /// </remarks>
    private static async Task APathInsideAVolumeIsNotAVolumeToEmpty(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("a real entry on the volume for a silent widening to destroy");

        var file = workspace.CreateRandomFile(4096);

        // Something on the volume, so a silent widening would have something to destroy.
        var recycled = await FileRecycler.MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "there is an entry on the volume for the check to be about");

        var (countBefore, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");

        var result = await RecycleBin.EmptyAsync(ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, result.VolumesEmptied.Count, "a directory inside a volume is not a volume that got emptied");
        Check.Equal(0, result.ItemsRemoved, "and nothing was removed in answer to it");
        Check.Equal(1, result.Failures.Count, "the caller is told the path was not one");

        var failure = result.Failures[0];
        Check.Equal(RecycleFailureStage.PathValidation, failure.Stage, "as a path that was not a volume root");
        Check.Equal(ScratchWorkspace.Root, failure.Path, "naming the path the caller passed");

        var (countAfter, _) = RecycleBinProbe.Measure(ScratchWorkspace.VolumeRoot);
        Check.Equal(countBefore, countAfter, "and the volume the path lives in is untouched");

        workspace.Dispose();
    }

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
        ScratchWorkspace.RequireRecycling("an entry in the bin to read back after the empty");

        var files = workspace.CreateRandomFiles(1, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var (countBefore, _) = RecycleBinProbe.MeasureOrFail(volumeRoot, "the baseline count");

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        Check.True(
            RecycleBinProbe.Measure(volumeRoot).Items - countBefore >= 1,
            "the file reached the Recycle Bin before emptying");

        var emptied = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        var after = await RecycleBin.GetItemsAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);
        Check.False(
            after.Items.Any(i => string.Equals(i.OriginalPath, files[0], StringComparison.OrdinalIgnoreCase)),
            "the file is no longer in the Recycle Bin");
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
        ScratchWorkspace.RequireRecycling("entries in the bin for the counts to be taken from");

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
        ScratchWorkspace.RequireRecycling("an entry in the bin for the first empty to find");

        var files = workspace.CreateRandomFiles(1, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        // The premise of the case is a Recycle Bin that was not empty, so the run has to establish one
        // rather than assume it: without these the case passes on a bin nothing ever reached, and the
        // "already empty" it is about is one the test made for itself by failing quietly.
        Check.True(recycled.Succeeded, $"the file reached the bin before the empty (first failure: {recycled.FirstFailure?.Stage} 0x{recycled.FirstFailure?.HResult:X8})");
        Check.Equal(files.Count, recycled.RecycledPaths.Count, "so the volume's Recycle Bin was not empty to begin with");

        var first = await RecycleBin.EmptyAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, first.Failures.Count, $"the first empty reached the volume (failures: {string.Join(", ", first.Failures.Select(f => $"{f.Stage} 0x{f.HResult:X8}"))})");
        Check.True(first.ItemsRemoved >= files.Count, $"and it removed what was in it (got {first.ItemsRemoved})");

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
        var letter = AbsentVolume.Letter();
        AbsentVolume.RequireAbsent(letter, "emptying a volume that is not there");
        var emptied = await RecycleBin.EmptyAsync(AbsentVolume.RootOf(letter), context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, emptied.ItemsRemoved, "an absent volume removes nothing");
        Check.Equal(0, emptied.BytesFreed, "an absent volume frees nothing");
        Check.True(emptied.Failures.Count > 0, $"the absent volume is reported rather than swallowed (got {emptied.Failures.Count} failure(s))");
    }
}

using System.Diagnostics;
using System.Runtime.Versioning;
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Owns the questions about one volume at a time: what its Recycle Bin holds, and what this library
/// does when a volume has none.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class VolumeSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("VolumeSuite." + nameof(RecycleBinQueryReadsTrackedItems), RecycleBinQueryReadsTrackedItems),
        new TestCase("VolumeSuite." + nameof(RecycleBinQueryReportsFailureForMissingVolume), RecycleBinQueryReportsFailureForMissingVolume),
        new TestCase("VolumeSuite." + nameof(AVolumeWithoutARecycleBinIsRefused), AVolumeWithoutARecycleBinIsRefused, TestKind.Manual),
        new TestCase(
            "VolumeSuite." + nameof(AVolumeWithoutARecycleBinCanBeDeletedOnRequest),
            AVolumeWithoutARecycleBinCanBeDeletedOnRequest,
            TestKind.Manual),
        new TestCase(
            "VolumeSuite." + nameof(AStatusAndAnEmptyReadTheSameRootTheSameWay),
            AStatusAndAnEmptyReadTheSameRootTheSameWay,
            TestKind.Manual),
    ];

    /// <summary>
    /// A delta assertion needs two numbers, not the entries behind them, so the probe must not enumerate.
    /// </summary>
    /// <remarks>
    /// The probe used to answer "did the count go up" by listing the Recycle Bin, which costs six COM
    /// round trips and a stat per entry. With ninety thousand files in the bin that made one query take
    /// over thirteen minutes, and the suites ask for it twice per test — the whole run was gated on how
    /// full the caller's bin happened to be. The counter brings that to about eleven seconds, because the
    /// Shell still walks the bin's metadata but nothing here walks it twice.
    /// <para>
    /// The elapsed check below is the guard against going back. The bound is nowhere near what the
    /// counter costs: it separates two answers that differ by seventy times and is not trying to measure
    /// the counter, so nothing this machine produces from one lands in the wrong place. It cannot be
    /// tightened without turning the test into a timing test.
    /// </para>
    /// </remarks>
    private static Task RecycleBinQueryReadsTrackedItems(TestContext context)
    {
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var (items, size) = RecycleBinProbe.Measure(volumeRoot);

        // The Shell counts only items it tracks, so an empty or partially purged bin is a normal
        // state and the count cannot be asserted to be positive. What must hold is that the query
        // succeeds, is repeatable, and never reports a negative value.
        Check.True(items >= 0, $"item count is non-negative (got {items})");
        Check.True(size >= 0, $"size is non-negative (got {size})");

        var stopwatch = Stopwatch.StartNew();
        var repeated = RecycleBinProbe.Measure(volumeRoot);
        stopwatch.Stop();

        Check.Equal(items, repeated.Items, "repeated query returns the same item count");
        Check.Equal(size, repeated.Bytes, "repeated query returns the same size");
        Check.True(
            stopwatch.ElapsedMilliseconds < CounterBudgetMs,
            $"reading the counter does not enumerate the bin (took {stopwatch.ElapsedMilliseconds} ms, budget {CounterBudgetMs} ms)");

        Console.WriteLine($"        E:\\ tracked items: {items}, {size} byte(s), query {stopwatch.ElapsedMilliseconds} ms");

        return Task.CompletedTask;
    }

    private static Task RecycleBinQueryReportsFailureForMissingVolume(TestContext context)
    {
        // Guards the -1 sentinel: without it a failed query would be indistinguishable from an
        // empty bin, and every receipt assertion downstream would silently pass.
        Check.Equal(-1L, RecycleBinProbe.QueryItemCount("Z:\\"), "querying an absent volume reports failure");
        Check.Equal(-1L, RecycleBinProbe.QueryItemCount("Z:\\"), "failure sentinel is stable across calls");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A volume with no Recycle Bin must not lose the file while the library reports a successful
    /// recycle, so the path is refused before the Shell is asked and the file is left where the caller
    /// put it.
    /// </summary>
    /// <remarks>
    /// This is the claim on real hardware, on the one shape of volume where the Shell's own behaviour is
    /// the danger: it removes a file handed to it for good and still reports success. The refusal itself
    /// is covered in the default run by <c>FacadeSuite.RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff</c>,
    /// over a <c>subst</c> mapping — a stand-in for a removable drive, not the thing itself. What only a
    /// real volume adds is the Shell's own refusal code on a volume Windows never configured a bin for.
    /// <para>
    /// Manual because it destroys files on a path the caller names, and because nothing in this
    /// repository can make a removable volume appear. A healthy volume is not a weaker form of the same
    /// volume: it is a different question, so this goes red there rather than passing — see
    /// <see cref="RequireAVolumeWithoutARecycleBin"/>.
    /// </para>
    /// </remarks>
    private static async Task AVolumeWithoutARecycleBinIsRefused(TestContext context)
    {
        var volume = await RequireAVolumeWithoutARecycleBin(context).ConfigureAwait(false);
        var workspace = CreateManualDirectory(volume.Root);

        try
        {
            var file = Path.Combine(workspace, "refused.bin");
            File.WriteAllBytes(file, new byte[4096]);

            var options = new RecycleBinOptions { ThrowOnError = false };

            var result = await FileRecycler
                .MoveToRecycleBinAsync([file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.False(
                result.Succeeded,
                $"a volume with no Recycle Bin cannot report success (recycled {result.RecycledPaths.Count})");

            var failure = result.FirstFailure;
            Check.NotNull(failure, "the refused path is reported rather than swallowed");
            Check.Equal(1, result.Failures.Count, "and it is the only failure reported");
            Check.Equal(RecycleFailureStage.NoRecycleBinOnVolume, failure!.Stage, "naming the volume, not the Shell's doing");
            Check.Equal(file, failure.Path, "naming the caller's path");
            Check.Equal(0, failure.Attempts, "nothing was attempted, because nothing was handed over");

            // Which code the refusal carries follows from how the reason was settled, and only one of the
            // two ways leaves the Shell with something to say: a volume whose type rules recycling out was
            // never asked, so there is no code of its to report.
            if (volume.Reason is RecycleBinAvailabilityReason.NoRecycleBinOnVolume)
            {
                Check.Equal(
                    volume.HResult,
                    failure.HResult,
                    $"a refusal the Shell itself stated carries its own code, and this read it independently (got 0x{failure.HResult:X8})");
            }
            else
            {
                Check.Equal(
                    0,
                    failure.HResult,
                    $"a refusal settled from configuration carries no code of the Shell's (got 0x{failure.HResult:X8})");
            }

            Check.Equal(0, result.RecycledPaths.Count, "a refused path is not reported as recycled");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "nor as removed for good");
            Check.True(File.Exists(file), "the file is still on disk, which is the whole point");
        }
        finally
        {
            RemoveManualDirectory(workspace);
        }
    }

    /// <summary>
    /// The caller's other answer on the same volume: remove the file for good, and say so in the one list
    /// that says so.
    /// </summary>
    /// <remarks>
    /// The same refusal as <see cref="AVolumeWithoutARecycleBinIsRefused"/> is what a caller who never
    /// asked for this gets; this is its opposite on the very same volume.
    /// <see cref="RecycleBinResult.RecycledPaths"/> is asserted empty rather than merely smaller, because
    /// a path in both lists would have the two contradict each other.
    /// <para>
    /// Covered in the default run by <c>FacadeSuite.DeletePermanentlyRemovesTheFileAndNamesIt</c> over a
    /// <c>subst</c> mapping. Manual for the same two reasons as
    /// <see cref="AVolumeWithoutARecycleBinIsRefused"/>, and it is the case that costs the caller data:
    /// it deletes the file it created, on the path the caller named.
    /// </para>
    /// </remarks>
    private static async Task AVolumeWithoutARecycleBinCanBeDeletedOnRequest(TestContext context)
    {
        var volume = await RequireAVolumeWithoutARecycleBin(context).ConfigureAwait(false);
        var workspace = CreateManualDirectory(volume.Root);

        try
        {
            var file = Path.Combine(workspace, "signed-away.bin");
            File.WriteAllBytes(file, new byte[4096]);

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                NoRecycleBinAction = NoRecycleBinAction.DeletePermanently,
            };

            var result = await FileRecycler
                .MoveToRecycleBinAsync([file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.True(
                result.Succeeded,
                $"the caller asked for the file to go (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");

            Check.Equal(1, result.PermanentlyDeletedPaths.Count, "the removed path is named as removed");
            Check.Equal(file, result.PermanentlyDeletedPaths[0], "in the spelling the caller supplied");
            Check.Equal(0, result.RecycledPaths.Count, "and it is not also reported as recycled");
            Check.Equal(0, result.Failures.Count, "removing a file the caller signed away is not a failure");
            Check.False(File.Exists(file), "the file is gone from disk, which is the whole point of the option");
        }
        finally
        {
            RemoveManualDirectory(workspace);
        }
    }

    /// <summary>
    /// The status and the empty both decide what a volume root is, so one string asked about both has to
    /// get one answer. A share root is the spelling that tells them apart.
    /// </summary>
    /// <remarks>
    /// <see cref="RecycleBin"/> used to hold a second copy of that predicate, and the copy compared the
    /// caller's spelling as typed — so <c>EmptyAsync(@"\\server\share\")</c> refused a path
    /// <c>GetStatusAsync(@"\\server\share\")</c> answered for. <c>Path.GetPathRoot</c> spells a share in
    /// backslashes whichever way the caller wrote it, so the two disagreed on exactly one family of
    /// roots and agreed on every drive letter, which is why a local volume cannot witness it.
    /// <see cref="Suites.EmptySuite.AForwardSlashSpellingIsTheVolumeToEmpty"/> holds the same agreement on
    /// a drive; only the share has to be asked for.
    /// <para>
    /// Manual because the answer cannot be reached without a real share: once the empty accepts a share it
    /// hands it to the Shell and queries that share's bin, which is a question for a name that may turn
    /// out to be somebody's real share. What keeps that safe is the precondition below, which will not run
    /// this on a volume the Shell does answer for — there the empty would remove the caller's own recycled
    /// files, and no assertion made afterwards would be worth having run.
    /// </para>
    /// </remarks>
    private static async Task AStatusAndAnEmptyReadTheSameRootTheSameWay(TestContext context)
    {
        var volume = await RequireAVolumeWithoutARecycleBin(context).ConfigureAwait(false);

        var status = await RecycleBin.GetStatusAsync(volume.Root, context.CancellationToken).ConfigureAwait(false);
        var emptied = await RecycleBin.EmptyAsync(volume.Root, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(
            volume.Root,
            status.VolumeRoot,
            "the status names the root the caller asked about, so there is one string to compare on");
        Check.False(status.IsEnabled, $"the status says the volume does not recycle (got {status.Reason})");
        Check.Equal(0, emptied.VolumesEmptied.Count, $"and the empty removes nothing (failures: {Describe(emptied.Failures)})");
        Check.Equal(0, emptied.ItemsRemoved, "with nothing removed to report");
        Check.True(
            emptied.Failures.All(failure => failure.Stage != RecycleFailureStage.PathValidation),
            $"the empty reads {volume.Root} as the volume root the status named, rather than refusing it as a " +
            $"path inside a volume (failures: {Describe(emptied.Failures)})");
    }

    /// <summary>
    /// A volume this group is willing to act on: the root the status reported, the reason it gives that the
    /// volume cannot recycle, and the code the Shell gave for failing to answer a question about that
    /// volume's Recycle Bin.
    /// </summary>
    /// <param name="Root">
    /// The root <see cref="RecycleBin.GetStatusAsync"/> reported, always with its trailing separator. Every
    /// call below is made with this string rather than with the spelling <c>--manual</c> named, so the oracle,
    /// the empty and the Shell are all asked about one root.
    /// </param>
    /// <param name="HResult">The Shell's own code for failing to answer a question about that volume's Recycle Bin.</param>
    /// <param name="Reason">Why <see cref="RecycleBin.GetStatusAsync"/> says the volume cannot recycle.</param>
    private sealed record VolumeWithNoRecycleBin(
        string Root,
        RecycleBinAvailabilityReason Reason,
        int HResult);

    /// <summary>
    /// The root <c>--manual</c> named, checked against the library's reading of it and against the Shell's
    /// answer about its Recycle Bin before anything is created there or handed to the Shell.
    /// </summary>
    /// <remarks>
    /// Two answers have to line up before a case in this group does anything, and neither is a formality.
    /// The library's own reading settles the reason. The Shell's, asked directly about the same string, is
    /// what keeps the group off a volume whose Recycle Bin answers — which is what stops the empty from
    /// removing the caller's recycled files, and what stops the other two from reasoning about a volume that
    /// recycles.
    /// <para>
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/> is admitted only when the Shell has no
    /// answer either. That reason covers two opposite facts — a volume whose type could not be read, and a
    /// volume the Shell found a bin on — and the direct query is what tells them apart.
    /// </para>
    /// <para>
    /// What the direct query establishes is narrow, and is claimed no wider than that: the Shell did not
    /// answer a question about this volume at this spelling. That is true both of a volume with no Recycle
    /// Bin and of one it cannot be asked about at all — an unreachable share, a denial — and the two are not
    /// separable from here. It is also a near-clone of the library's own probe rather than a second
    /// implementation: a different assembly calling the same API.
    /// </para>
    /// <para>
    /// The empty is gated on that pairing rather than on the fact, and cannot do better: it hands the Shell a
    /// root to empty without consulting a counter first, so "the Shell did not answer here" is what stands in
    /// for "there is nothing to empty". That is the limit of this gate, stated where the gate is.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The runner selected this case without a root.</exception>
    /// <exception cref="CheckFailedException">The root is not a volume with no Recycle Bin.</exception>
    private static async Task<VolumeWithNoRecycleBin> RequireAVolumeWithoutARecycleBin(TestContext context)
    {
        var requested = context.ManualRoot
            ?? throw new InvalidOperationException(
                "A manual case is run once per --manual root; this one was given none.");

        var status = await RecycleBin.GetStatusAsync(requested, context.CancellationToken).ConfigureAwait(false);

        // Asked about the root the library itself reports, which is the string EmptyAsync hands the Shell,
        // rather than about the caller's spelling: a gate that probes one string and acts on another is the
        // failure this suite exists to catch, and it would catch it in its own setup first.
        var bin = RecycleBinVolume.Measure(status.VolumeRoot);

        Console.WriteLine(
            $"        {requested} reads as {status.Reason}{(status.VolumeRoot == requested ? "" : $" as {status.VolumeRoot}")}"
            + (bin.Ok
                ? $", and the Shell answered for its Recycle Bin with {bin.Items} item(s)"
                : $", and the Shell did not answer about its Recycle Bin (0x{bin.HResult:X8})"));

        var librarySaysItCannotRecycle = status.Reason is RecycleBinAvailabilityReason.NoRecycleBinOnVolume
            or RecycleBinAvailabilityReason.UnsupportedVolumeType
            or RecycleBinAvailabilityReason.Indeterminate;

        if (librarySaysItCannotRecycle && !bin.Ok)
        {
            return new VolumeWithNoRecycleBin(status.VolumeRoot, status.Reason, bin.HResult);
        }

        var whatItIs = bin.Ok
            ? $"the Shell answered for its Recycle Bin with {bin.Items} item(s), so this is a volume that does recycle"
            : $"the Shell did not answer about its Recycle Bin either (0x{bin.HResult:X8}), so it is not a volume that cannot recycle";

        throw new CheckFailedException(
            $"{requested} reads as {status.Reason}, and {whatItIs}. A case in this group needs a volume with no " +
            @"Recycle Bin: a removable drive with no $RECYCLE.BIN, or a network share written as ""\\server\share\"". " +
            "This is red on purpose rather than skipped — a caller who named this path asked a question it " +
            "cannot answer, and a pass would claim coverage that was never provided.");
    }

    /// <summary>Prefix of the directory each manual case creates for itself, so a leftover is identifiable.</summary>
    private const string ManualDirectoryPrefix = "RecycleBinToolkit.Manual";

    private static int _manualSequence;

    /// <summary>
    /// Creates a directory of this case's own on the path the caller named, and returns its full path.
    /// </summary>
    /// <remarks>
    /// The caller owns the root and may have anything at all in it, so the case adds one directory of its
    /// own rather than writing beside it. Only that directory is ever removed, and only by full path.
    /// </remarks>
    private static string CreateManualDirectory(string root)
    {
        // A separator is added rather than one trimmed off. Path.Combine("F:", name) is the drive-relative
        // "F:name", and a directory made under it belongs to whatever current directory the process happens
        // to have for that drive — so both where the case writes and what its cleanup deletes would be
        // decided by ambient state rather than by the root the caller named. A bare "F:" is drive-relative
        // for the same reason, which is why the separator is added rather than required.
        var parent = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var path = Path.Combine(
            parent,
            $"{ManualDirectoryPrefix}-{Environment.ProcessId}-{Interlocked.Increment(ref _manualSequence)}");

        // Asserted rather than assumed, because this is the one place where a drive letter and a root with
        // a separator differ by nothing visible: the directory would be created on the right volume by
        // luck, and only the path this returns says whether it was.
        if (!Path.IsPathFullyQualified(path))
        {
            throw new CheckFailedException($"the workspace path {path} built from root {root} is not a full path");
        }

        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>
    /// Removes a directory this case created, reporting a cleanup it could not finish rather than
    /// throwing from the <c>finally</c> that called it.
    /// </summary>
    /// <remarks>
    /// Throwing here would replace whatever the case was actually reporting with the cleanup's own
    /// complaint. Printing keeps the case's result intact while still naming a directory left on the
    /// caller's volume.
    /// <para>
    /// A path that is not fully qualified is refused rather than resolved, because resolving one is what
    /// would turn a case's own directory into whatever the current directory says it is.
    /// </para>
    /// </remarks>
    private static void RemoveManualDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            Console.WriteLine($"        WARNING: refusing to remove {path}, which is not a full path");
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Console.WriteLine($"        WARNING: left {path} behind -- {ex.Message}");
        }
    }

    /// <summary>Failures as one readable string, so a failed assertion says what it was competing with.</summary>
    private static string Describe(IReadOnlyList<RecycleFailure> failures) =>
        failures.Count == 0
            ? "none"
            : string.Join(", ", failures.Select(failure => $"{failure.Stage} 0x{failure.HResult:X8}"));

    /// <summary>Separates the counter from a listing, which is a difference of about seventy times here.</summary>
    private const int CounterBudgetMs = 120_000;
}
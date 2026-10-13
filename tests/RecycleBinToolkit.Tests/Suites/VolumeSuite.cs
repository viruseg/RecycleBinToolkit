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
[SupportedOSPlatform("windows10.0.14393")]
internal static class VolumeSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("VolumeSuite." + nameof(RecycleBinQueryReadsTrackedItems), RecycleBinQueryReadsTrackedItems),
        new TestCase("VolumeSuite." + nameof(RecycleBinQueryReportsFailureForMissingVolume), RecycleBinQueryReportsFailureForMissingVolume),
        new TestCase("VolumeSuite." + nameof(TheScratchVolumeReasonIsWhatTheLibraryReports), TheScratchVolumeReasonIsWhatTheLibraryReports),
        new TestCase("VolumeSuite." + nameof(ARootCarryingNonsenseIsRefusedRatherThanThrowing), ARootCarryingNonsenseIsRefusedRatherThanThrowing),
    ];

    /// <summary>
    /// A delta assertion needs two numbers, not the entries behind them, so the probe must not read records.
    /// </summary>
    /// <remarks>
    /// The probe answers "did the count go up" by walking the bin directory, which costs one walk rather
    /// than one record read per entry. With ninety thousand files in the bin that is the difference
    /// between a fast count and a listing's worth of reads, and the suites ask for it twice per test —
    /// the whole run must not be gated on how full the caller's bin happened to be.
    /// <para>
    /// The elapsed check below is the guard against going back. The bound is nowhere near what the
    /// walk costs: it separates two answers that differ by an order of magnitude and is not trying to
    /// measure the walk, so nothing this machine produces from one lands in the wrong place. It cannot be
    /// tightened without turning the test into a timing test.
    /// </para>
    /// </remarks>
    private static Task RecycleBinQueryReadsTrackedItems(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the counter to have entries to track");

        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var (items, size) = RecycleBinProbe.Measure(volumeRoot);

        // A walk counts what is there, so an empty or partially purged bin is a normal
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
            $"reading the count does not read records (took {stopwatch.ElapsedMilliseconds} ms, budget {CounterBudgetMs} ms)");

        Console.WriteLine($"        {volumeRoot} tracked items: {items}, {size} byte(s), query {stopwatch.ElapsedMilliseconds} ms");

        return Task.CompletedTask;
    }

    private static Task RecycleBinQueryReportsFailureForMissingVolume(TestContext context)
    {
        // Guards the -1 sentinel: without it a failed query would be indistinguishable from an
        // empty bin, and every delta assertion downstream would silently pass.
        var letter = AbsentVolume.Letter();
        AbsentVolume.RequireAbsent(letter, "querying a volume that is not there");

        Check.Equal(-1L, RecycleBinProbe.QueryItemCount(AbsentVolume.RootOf(letter)), "querying an absent volume reports failure");
        Check.Equal(-1L, RecycleBinProbe.QueryItemCount(AbsentVolume.RootOf(letter)), "failure sentinel is stable across calls");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The answer a run carries about its volume is still the library's answer about that volume, however
    /// long ago it was read.
    /// </summary>
    /// <remarks>
    /// What this rules out is drift: the cached value and a fresh inspection agree, so every case that
    /// reads <see cref="ScratchWorkspace.Reason"/> and every message naming the reason are talking about one
    /// state of the machine rather than about a mixture of readings taken at different times.
    /// <para>
    /// What it cannot rule out is where the value came from. A rule keyed on the path — enabled because a
    /// case wanted it to be, <c>DriveType.Removable</c> answered one way and everything else the other —
    /// would agree with the library on every volume the two answers happen to match, which on a healthy
    /// machine is all of them. Origin shows where they disagree, on a volume with no Recycle Bin: there
    /// the gate has to be reading the machine, or every case behind
    /// <see cref="ScratchWorkspace.RequireNonRecycling"/> skips on a volume that does recycle and the run
    /// reports no coverage without saying why.
    /// </para>
    /// <para>
    /// The volume is pinned through <see cref="ScratchWorkspace.Root"/> rather than through the string handed
    /// to the inspection, because that string is the caller's own spelling while
    /// <see cref="RecycleBinVolumeState.Inspect"/> answers with the volume as the library spells it — and
    /// <c>--scratch \\?\X:\</c> is a spelling this suite accepts. Both sides go through the same reduction
    /// here, so an extended-length root compares equal instead of redding over a prefix that says nothing
    /// about the volume.
    /// </para>
    /// </remarks>
    private static Task TheScratchVolumeReasonIsWhatTheLibraryReports(TestContext context)
    {
        var asked = RecycleBinVolumeState.Inspect(ScratchWorkspace.VolumeRoot);
        var worked = RecycleBinVolumeState.NormalizeVolumeRoot(ScratchWorkspace.Root);

        Check.Equal(asked.Reason, ScratchWorkspace.Reason, $"the run's answer still matches the library's own (got {ScratchWorkspace.Reason})");
        Check.Equal(asked.VolumeRoot, worked, "and both are about the volume this run works on");

        return Task.CompletedTask;
    }

    /// <summary>Separates the count from a listing, which is a difference of an order of magnitude here.</summary>
    private const int CounterBudgetMs = 120_000;

    /// <summary>
    /// A volume root is a volume root or it is not, and a caller who types nonsense into one gets the
    /// answer that names it — never an exception from three frames down inside the path API.
    /// </summary>
    /// <remarks>
    /// A NUL is the character worth probing: it is what a caller most plausibly carries without meaning to
    /// (a concatenated argument), and it is the one character that cannot be in any real path, so nothing
    /// past the root check can be reached by it.
    /// <para>
    /// The answer is the same for all three operations that take a root, which is what makes it a rule
    /// rather than three coincidences: <see cref="RecycleBin.GetStatusAsync"/> names the volume as not
    /// found, and the two that act name the path as one that was not a volume root — the same
    /// <see cref="RecycleFailureStage.PathValidation"/> each already returns for any other path inside a
    /// volume. <see cref="RecycleBin.GetItemsAsync"/> is included because refusing a non-root there is
    /// recent, and a caller's first sign of that rule would be an exception rather than an empty listing.
    /// </para>
    /// <para>
    /// Nothing here can create or remove anything: none of the three writes to the volume the nonsense
    /// names, because none of them gets as far as resolving one out of it.
    /// </para>
    /// </remarks>
    private static async Task ARootCarryingNonsenseIsRefusedRatherThanThrowing(TestContext context)
    {
        const string NulInRoot = "W:\0";

        var status = await RecycleBin.GetStatusAsync(NulInRoot, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(
            RecycleBinAvailabilityReason.VolumeNotFound,
            status.Reason,
            "a status of a root carrying a NUL names it as a volume that is not there rather than raising");

        var listed = await RecycleBin.GetItemsAsync(NulInRoot, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(0, listed.Items.Count, "a listing of one returns nothing, because no volume was resolved to list");
        Check.Equal(1, listed.Failures.Count, "and says the path was not a volume root");
        Check.Equal(
            RecycleFailureStage.PathValidation,
            listed.Failures[0].Stage,
            "at the stage the same refusal already arrives with, rather than raising from the path API");

        var emptied = await RecycleBin.EmptyAsync(NulInRoot, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(0, emptied.VolumesEmptied.Count, "an empty of one empties no volume");
        Check.Equal(0, emptied.ItemsRemoved, "and removes nothing");
        Check.Equal(1, emptied.Failures.Count, "and says the path was not a volume root");
        Check.Equal(
            RecycleFailureStage.PathValidation,
            emptied.Failures[0].Stage,
            "the same way the listing refused it, so all three answer one input the same way");
    }
}
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
        new TestCase("VolumeSuite." + nameof(TheScratchVolumeReasonIsWhatTheLibraryReports), TheScratchVolumeReasonIsWhatTheLibraryReports),
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
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the counter to have entries to track");

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

        Console.WriteLine($"        {volumeRoot} tracked items: {items}, {size} byte(s), query {stopwatch.ElapsedMilliseconds} ms");

        return Task.CompletedTask;
    }

    private static Task RecycleBinQueryReportsFailureForMissingVolume(TestContext context)
    {
        // Guards the -1 sentinel: without it a failed query would be indistinguishable from an
        // empty bin, and every receipt assertion downstream would silently pass.
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
    /// <c>--scratch \\?\E:\</c> is a spelling this suite accepts. Both sides go through the same reduction
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

    /// <summary>Separates the counter from a listing, which is a difference of about seventy times here.</summary>
    private const int CounterBudgetMs = 120_000;
}
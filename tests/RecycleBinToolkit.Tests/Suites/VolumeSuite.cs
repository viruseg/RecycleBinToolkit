using System.Diagnostics;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

internal static class VolumeSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("VolumeSuite." + nameof(RecycleBinQueryReadsTrackedItems), RecycleBinQueryReadsTrackedItems),
        new TestCase("VolumeSuite." + nameof(RecycleBinQueryReportsFailureForMissingVolume), RecycleBinQueryReportsFailureForMissingVolume),
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

    /// <summary>Separates the counter from a listing, which is a difference of about seventy times here.</summary>
    private const int CounterBudgetMs = 120_000;
}
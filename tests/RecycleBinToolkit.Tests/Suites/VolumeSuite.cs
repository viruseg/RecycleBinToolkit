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

    private static Task RecycleBinQueryReadsTrackedItems(TestContext context)
    {
        var items = RecycleBinVolume.QueryItemCount(@"E:\");
        var size = RecycleBinVolume.QuerySize(@"E:\");

        // The Shell counts only items it tracks, so an empty or partially purged bin is a normal
        // state and the count cannot be asserted to be positive. What must hold is that the query
        // succeeds, is repeatable, and never reports a negative value.
        Check.True(items >= 0, $"item count is non-negative (got {items})");
        Check.True(size >= 0, $"size is non-negative (got {size})");
        Check.Equal(items, RecycleBinVolume.QueryItemCount(@"E:\"), "repeated query returns the same item count");
        Check.Equal(size, RecycleBinVolume.QuerySize(@"E:\"), "repeated query returns the same size");

        Console.WriteLine($"        E:\\ tracked items: {items}, {size} byte(s)");

        return Task.CompletedTask;
    }

    private static Task RecycleBinQueryReportsFailureForMissingVolume(TestContext context)
    {
        // Guards the -1 sentinel: without it a failed query would be indistinguishable from an
        // empty bin, and every receipt assertion downstream would silently pass.
        Check.Equal(-1L, RecycleBinVolume.QueryItemCount("Z:\\"), "querying an absent volume reports failure");
        Check.Equal(-1L, RecycleBinVolume.QueryItemCount("Z:\\"), "failure sentinel is stable across calls");

        return Task.CompletedTask;
    }
}
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
            Report(left, $"X:\\left-{i}.bin");
            Report(right, $"X:\\right-{i}.bin");
        }

        await left.CompleteAsync(wait: true).ConfigureAwait(false);
        await right.CompleteAsync(wait: true).ConfigureAwait(false);

        Check.Equal("1, 2, 3, 4", string.Join(", ", first), "one call numbers its own items from 1");
        Check.Equal("1, 2, 3, 4", string.Join(", ", second), "and so does the other, separately");
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
}
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class WorkflowSuite
{
    private const int SliceBytes = 4096;
    private const int AppendBytes = 1024;

    /// <summary>How many touched files the load case moves through the Shell.</summary>
    private const int LoadFileCount = 20;

    /// <summary>
    /// How many the ordinary case touches. Small enough that the default run pays nothing for it, and
    /// large enough that the batch is a batch: one path would exercise the single-path overload instead.
    /// </summary>
    private const int OrdinaryFileCount = 5;

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("WorkflowSuite." + nameof(ReadPartialAndAppendThenRecycle), ReadPartialAndAppendThenRecycle, TestKind.Load),
        new TestCase("WorkflowSuite." + nameof(FiveTouchedFilesAreAllRecycled), FiveTouchedFilesAreAllRecycled),
    ];

    /// <summary>
    /// Emulates what a real program does with a file before deleting it: read it whole, read a slice
    /// from the middle, then append. Every stream is scoped in a <c>using</c>, so the test itself
    /// never holds a handle — otherwise it would manufacture the very failure it is hunting.
    /// </summary>
    /// <remarks>
    /// Load, because the files come out of the full 94-file fixture set. The claim it makes is not: see
    /// <see cref="FiveTouchedFilesAreAllRecycled"/>, which makes the same assertions in the default run.
    /// </remarks>
    private static async Task ReadPartialAndAppendThenRecycle(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var all = workspace.CreateFixtureSet();
        var random = new Random(20261006);
        var chosen = all.OrderBy(_ => random.Next()).Take(LoadFileCount).ToArray();

        await TouchThenRecycle(chosen, context);

        workspace.Dispose();
    }

    /// <summary>
    /// The same claims as <see cref="ReadPartialAndAppendThenRecycle"/> at a size the default run can
    /// afford, so a program that touches its files before deleting them is covered whether or not anyone
    /// asked for the load group.
    /// </summary>
    private static async Task FiveTouchedFilesAreAllRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var chosen = workspace.CreateFixtureSet(OrdinaryFileCount);

        await TouchThenRecycle(chosen, context);

        workspace.Dispose();
    }

    /// <summary>
    /// Touches every path the way a program would, recycles the batch, and asserts what a caller can
    /// see afterwards: the call succeeded, every path is named as recycled, none is named as failed, the
    /// Recycle Bin grew by at least the batch, and nothing is left on disk.
    /// </summary>
    private static async Task TouchThenRecycle(IReadOnlyList<string> paths, TestContext context)
    {
        foreach (var path in paths)
        {
            Touch(path);
        }

        var before = RecycleBinProbe.QueryItemCount(@"E:\");

        var result = await FileRecycler.MoveToRecycleBinAsync(paths, cancellationToken: context.CancellationToken);
        var gained = RecycleBinProbe.QueryItemCount(@"E:\") - before;

        Check.True(result.Succeeded, $"every touched file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8} {result.FirstFailure?.Path})");
        Check.Equal(paths.Count, result.RecycledPaths.Count, "every touched file is reported as recycled");
        Check.Equal(0, result.Failures.Count, "no touched file is reported as failed");
        Check.True(gained >= paths.Count, $"the Recycle Bin gained every touched file (gained {gained}, expected at least {paths.Count})");

        foreach (var path in paths)
        {
            Check.False(File.Exists(path), $"{Path.GetFileName(path)} is gone from disk");
        }
    }

    private static void Touch(string path)
    {
        byte[] whole;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            whole = new byte[stream.Length];
            stream.ReadExactly(whole);
        }

        var sliceStart = whole.Length > SliceBytes ? (whole.Length / 2) - (SliceBytes / 2) : 0;
        var sliceLength = Math.Min(SliceBytes, whole.Length - sliceStart);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(sliceStart, SeekOrigin.Begin);
            Span<byte> slice = stackalloc byte[SliceBytes];
            var read = stream.Read(slice[..sliceLength]);
            GC.KeepAlive(read);
        }

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var tail = new byte[AppendBytes];
            Random.Shared.NextBytes(tail);
            stream.Write(tail);
        }
    }
}
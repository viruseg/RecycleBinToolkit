using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class WorkflowSuite
{
    private const int SliceBytes = 4096;
    private const int AppendBytes = 1024;

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("WorkflowSuite." + nameof(ReadPartialAndAppendThenRecycle), ReadPartialAndAppendThenRecycle),
    ];

    /// <summary>
    /// Emulates what a real program does with a file before deleting it: read it whole, read a slice
    /// from the middle, then append. Every stream is scoped in a <c>using</c>, so the test itself
    /// never holds a handle — otherwise it would manufacture the very failure it is hunting.
    /// </summary>
    private static async Task ReadPartialAndAppendThenRecycle(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var all = workspace.CopyAllFixtures();
        var random = new Random(20261006);
        var chosen = all.OrderBy(_ => random.Next()).Take(20).ToArray();

        foreach (var path in chosen)
        {
            Touch(path);
        }

        var before = RecycleBinVolume.QueryItemCount(@"E:\");

        var result = await FileRecycler.MoveToRecycleBinAsync(chosen, cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"every touched file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8} {result.FirstFailure?.Path})");
        Check.Equal(chosen.Length, result.RecycledPaths.Count, "every touched file is reported as recycled");
        Check.Equal(0, result.Failures.Count, "no touched file is reported as failed");
        Check.True(RecycleBinVolume.QueryItemCount(@"E:\") - before >= chosen.Length, "the Recycle Bin gained every touched file");

        foreach (var path in chosen)
        {
            Check.False(File.Exists(path), $"{Path.GetFileName(path)} is gone from disk");
        }

        workspace.Dispose();
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
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Acceptance gate for the "100 % of files reach the Recycle Bin" requirement.
/// </summary>
internal static class StressSuite
{
    private const int Cycles = 10;
    private const int AppendBytes = 1024;

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("StressSuite." + nameof(TenCopyRecycleCyclesAlwaysSucceed), TenCopyRecycleCyclesAlwaysSucceed),
        new TestCase("StressSuite." + nameof(RecycledBytesReachTheBin), RecycledBytesReachTheBin),
    ];

    private static async Task TenCopyRecycleCyclesAlwaysSucceed(TestContext context)
    {
        var random = new Random(1337);
        var totalRecycled = 0;

        using var workspace = new ScratchWorkspace();

        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            workspace.Recreate();
            var available = workspace.CreateFixtureSet();
            var take = random.Next(10, Math.Min(31, available.Count + 1));
            var chosen = available.OrderBy(_ => random.Next()).Take(take).ToArray();

            foreach (var path in chosen)
            {
                ReadAndAppend(path, random);
            }

            var before = RecycleBinProbe.QueryItemCount(@"E:\");

            var result = await FileRecycler.MoveToRecycleBinAsync(
                chosen,
                cancellationToken: context.CancellationToken);

            Check.True(result.Succeeded, DescribeFailure(cycle, result));
            Check.Equal(0, result.Failures.Count, DescribeFailure(cycle, result));
            Check.Equal(chosen.Length, result.RecycledPaths.Count, $"cycle {cycle} recycled every chosen file");
            Check.True(RecycleBinProbe.QueryItemCount(@"E:\") - before >= chosen.Length, $"cycle {cycle} moved every chosen file into the Recycle Bin");

            foreach (var path in chosen)
            {
                Check.False(File.Exists(path), $"cycle {cycle}: {Path.GetFileName(path)} is gone from disk");
            }

            totalRecycled += chosen.Length;
        }

        workspace.Dispose();

        Console.WriteLine($"        {Cycles} cycles, {totalRecycled} file(s) recycled, zero failures");
        Check.True(totalRecycled > 0, "the stress run actually recycled files");
    }

    private static async Task RecycledBytesReachTheBin(TestContext context)
    {
        const int fileCount = 6;
        const int fileSize = 1024 * 1024;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var files = workspace.CreateRandomFiles(fileCount, fileSize);
        var (countBefore, sizeBefore) = RecycleBinProbe.Measure(@"E:\");

        var result = await FileRecycler.MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken);

        var (countAfter, sizeAfter) = RecycleBinProbe.Measure(@"E:\");

        Console.WriteLine($"        bin count {countBefore} -> {countAfter}, size {sizeBefore} -> {sizeAfter}");

        Check.True(result.Succeeded, DescribeFailure(0, result));
        Check.True(countAfter - countBefore >= fileCount, $"the Recycle Bin gained at least {fileCount} items");
        Check.True(sizeAfter - sizeBefore >= (long)fileCount * fileSize, $"the Recycle Bin grew by at least {fileCount} MiB, so the bytes were really moved rather than discarded");

        foreach (var path in files)
        {
            Check.False(File.Exists(path), $"{Path.GetFileName(path)} is gone from disk");
        }

        workspace.Dispose();
    }

    private static void ReadAndAppend(string path, Random random)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Span<byte> buffer = stackalloc byte[4096];
            var read = stream.Read(buffer);
            GC.KeepAlive(read);
        }

        var tail = new byte[AppendBytes];
        random.NextBytes(tail);

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write(tail);
        }
    }

    private static string DescribeFailure(int cycle, RecycleBinResult result)
    {
        var first = result.FirstFailure;
        return first is null
            ? $"cycle {cycle} reported no failures"
            : $"cycle {cycle}: {first.Stage} 0x{first.HResult:X8} after {first.Attempts} attempt(s) for '{first.Path}'";
    }
}
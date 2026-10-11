using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
    /// Acceptance gate for the "100 % of files reach the Recycle Bin" requirement.
    /// </summary>
    /// <remarks>
    /// Both cases are <see cref="TestKind.Load"/>, so they run only under <c>--load</c>. What they add to
    /// what ordinary cases already prove is that the guarantee survives repetition and volume; neither
    /// claim is the only coverage of anything. The cycles rest on
    /// <c>WorkflowSuite.FiveTouchedFilesAreAllRecycled</c> and <c>EmptySuite.EmptyReportsWhatItFreed</c>,
    /// and the bytes on <c>FacadeSuite.LargeFileIsRecycled</c>.
    /// </remarks>
    internal static class StressSuite
{
    private const int Cycles = 10;
    private const int AppendBytes = 1024;

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("StressSuite." + nameof(TenCopyRecycleCyclesAlwaysSucceed), TenCopyRecycleCyclesAlwaysSucceed, TestKind.Load),
        new TestCase("StressSuite." + nameof(RecycledBytesReachTheBin), RecycledBytesReachTheBin, TestKind.Load),
    ];

    private static async Task TenCopyRecycleCyclesAlwaysSucceed(TestContext context)
    {
        var random = new Random(1337);
        var totalRecycled = 0;

        using var workspace = new ScratchWorkspace();

        ScratchWorkspace.RequireRecycling("a Recycle Bin for the ten cycles to leave entries behind");

        // One pair of readings around the whole run rather than a pair per cycle. The Shell walks the
        // bin's metadata to answer, so a probe costs about a tenth of a second per thousand files
        // already in it: twenty probes meant the run length was set by how full the caller's bin was
        // rather than by anything under test. The guarantee is unchanged, and each cycle still names its
        // own failures, so a shortfall is attributed by the per-cycle result rather than by the delta.
        var (countBefore, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");

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

            var result = await RecycleBin.RecycleAsync(
                chosen,
                cancellationToken: context.CancellationToken);

            Check.True(result.Succeeded, DescribeFailure(cycle, result));
            Check.Equal(0, result.Failures.Count, DescribeFailure(cycle, result));
            Check.Equal(chosen.Length, result.RecycledPaths.Count, $"cycle {cycle} recycled every chosen file");

            foreach (var path in chosen)
            {
                Check.False(File.Exists(path), $"cycle {cycle}: {Path.GetFileName(path)} is gone from disk");
            }

            totalRecycled += chosen.Length;
        }

        var (countAfter, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the count after the cycles");
        Check.True(
            countAfter - countBefore >= totalRecycled,
            $"every file from every cycle reached the Recycle Bin (bin grew by {countAfter - countBefore}, {totalRecycled} recycled)");

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

        ScratchWorkspace.RequireRecycling("a Recycle Bin for the byte total to be measured");

        var files = workspace.CreateRandomFiles(fileCount, fileSize);
        var (countBefore, sizeBefore) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline bytes");

        var result = await RecycleBin.RecycleAsync(files, RecycleBinOptions.Default, cancellationToken: context.CancellationToken);

        var (countAfter, sizeAfter) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the bytes after the batches");

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
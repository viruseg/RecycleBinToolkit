using RecycleBinWrapper.Tests.Host;
using RecycleBinWrapper.Tests.Infrastructure;
using Utils;

namespace RecycleBinWrapper.Tests.Suites;

internal static class FacadeSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("FacadeSuite." + nameof(SingleFileIsRecycled), SingleFileIsRecycled),
        new TestCase("FacadeSuite." + nameof(BatchOfAllFixturesIsRecycled), BatchOfAllFixturesIsRecycled),
        new TestCase("FacadeSuite." + nameof(NullPathThrowsWhenThrowOnError), NullPathThrowsWhenThrowOnError),
        new TestCase("FacadeSuite." + nameof(NullPathFailsWithoutThrowOnError), NullPathFailsWithoutThrowOnError),
        new TestCase("FacadeSuite." + nameof(MissingPathDoesNotBlockTheRest), MissingPathDoesNotBlockTheRest),
        new TestCase("FacadeSuite." + nameof(EmptyBatchSucceeds), EmptyBatchSucceeds),
        new TestCase("FacadeSuite." + nameof(DirectoryTargetIsRecycledWithContents), DirectoryTargetIsRecycledWithContents),
        new TestCase("FacadeSuite." + nameof(LongPathIsRecycled), LongPathIsRecycled),
        new TestCase("FacadeSuite." + nameof(LargeFileIsRecycled), LargeFileIsRecycled),
        new TestCase("FacadeSuite." + nameof(CancellationIsObserved), CancellationIsObserved),
    ];

    private static async Task SingleFileIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var before = RecycleBinVolume.QueryItemCount(@"E:\");

        var recycled = await FileOperationApiWrapper.MoveToRecycleBinAsync(files[0], cancellationToken: context.CancellationToken);

        Check.True(recycled, "a single recycled file reports success");
        Check.False(File.Exists(files[0]), "the file is gone from disk");
        Check.True(RecycleBinVolume.QueryItemCount(@"E:\") - before >= 1, "the Recycle Bin gained the item");

        workspace.Dispose();
    }

    private static async Task BatchOfAllFixturesIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CopyAllFixtures();
        var before = RecycleBinVolume.QueryItemCount(@"E:\");

        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"every fixture is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8} {result.FirstFailure?.Path})");
        Check.Equal(files.Count, result.RecycledPaths.Count, "every fixture is reported as recycled");
        Check.Equal(0, result.Failures.Count, "no fixture is reported as failed");

        foreach (var file in files)
        {
            Check.False(File.Exists(file), $"{Path.GetFileName(file)} is gone from disk");
        }

        Check.True(RecycleBinVolume.QueryItemCount(@"E:\") - before >= files.Count, "the Recycle Bin gained every fixture");

        workspace.Dispose();
    }

    private static Task NullPathThrowsWhenThrowOnError(TestContext context)
    {
        Check.Throws<ArgumentException>(
            () => FileOperationApiWrapper.MoveToRecycleBinAsync((string?)null).GetAwaiter().GetResult(),
            "a null path with throwOnError is rejected eagerly");

        Check.Throws<ArgumentException>(
            () => FileOperationApiWrapper.MoveToRecycleBinAsync("   ").GetAwaiter().GetResult(),
            "a whitespace path with throwOnError is rejected eagerly");

        return Task.CompletedTask;
    }

    private static async Task NullPathFailsWithoutThrowOnError(TestContext context)
    {
        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync(
            new string?[] { null },
            cancellationToken: context.CancellationToken);

        Check.False(result.Succeeded, "a null path without throwOnError reports failure instead of throwing");
        Check.Equal(1, result.Failures.Count, "exactly one failure is reported");
        Check.Equal(RecycleFailureStage.PathValidation, result.Failures[0].Stage, "the failure is a path validation failure");
        Check.Equal(0, result.Failures[0].HResult, "no Shell code is involved in a validation failure");
        Check.Equal(0, result.RecycledPaths.Count, "nothing was recycled");
    }

    private static async Task MissingPathDoesNotBlockTheRest(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(5, 4096);
        var missing = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

        var batch = new List<string?> { missing };
        batch.AddRange(files);

        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync(batch, cancellationToken: context.CancellationToken);

        Check.False(result.Succeeded, "the missing path makes the batch report failure");
        Check.Equal(1, result.Failures.Count, "only the missing path is reported");
        Check.Equal(RecycleFailureStage.PathValidation, result.FirstFailure!.Stage, "the missing path fails at validation");
        Check.Equal(missing, result.FirstFailure.Path, "the failure names the missing path");
        Check.Equal(files.Count, result.RecycledPaths.Count, "every real file was still recycled");

        foreach (var file in files)
        {
            Check.False(File.Exists(file), $"{Path.GetFileName(file)} is gone from disk");
        }

        workspace.Dispose();
    }

    private static async Task EmptyBatchSucceeds(TestContext context)
    {
        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync(Array.Empty<string?>(), cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, "an empty batch is a success");
        Check.Equal(0, result.Failures.Count, "an empty batch reports no failures");
        Check.Equal(0, result.RecycledPaths.Count, "an empty batch recycles nothing");
    }

    private static async Task DirectoryTargetIsRecycledWithContents(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var directory = Path.Combine(ScratchWorkspace.Root, "tree", "nested");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(ScratchWorkspace.Root, "tree", "b.txt"), "world");

        var before = RecycleBinVolume.QueryItemCount(@"E:\");
        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync([directory], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a directory is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(Directory.Exists(directory), "the directory tree is gone from disk");
        Check.True(RecycleBinVolume.QueryItemCount(@"E:\") - before >= 1, "the Recycle Bin gained the directory");

        workspace.Dispose();
    }

    private static async Task LongPathIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var deep = Path.Combine(ScratchWorkspace.Root, new string('d', 120));
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, new string('f', 130) + ".bin");

        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(new byte[2048]);
        }

        Check.True(file.Length > 260, $"the test path is genuinely long ({file.Length} chars)");

        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a path over MAX_PATH is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(File.Exists(file), "the long-path file is gone from disk");

        workspace.Dispose();
    }

    private static async Task LargeFileIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        const int size = 10 * 1024 * 1024;
        var files = workspace.CreateRandomFiles(1, size);
        var before = RecycleBinVolume.QuerySize(@"E:\");

        var result = await FileOperationApiWrapper.MoveToRecycleBinAsync([files[0]], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a 10 MB file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(File.Exists(files[0]), "the 10 MB file is gone from disk");
        Check.True(RecycleBinVolume.QuerySize(@"E:\") - before >= size, "the Recycle Bin grew by the full 10 MB, so the bytes were really moved");

        workspace.Dispose();
    }

    private static async Task CancellationIsObserved(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(3, 4096);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Check.Throws<TaskCanceledException>(
            () => FileOperationApiWrapper.MoveToRecycleBinAsync(files, cancellationToken: cancelled.Token).GetAwaiter().GetResult(),
            "an already-cancelled token is observed instead of recycling");

        foreach (var file in files)
        {
            Check.True(File.Exists(file), $"{Path.GetFileName(file)} was left alone, as the call never started");
        }

        workspace.Dispose();
    }
}
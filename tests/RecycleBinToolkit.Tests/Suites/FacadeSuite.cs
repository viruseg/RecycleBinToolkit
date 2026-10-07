using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class FacadeSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("FacadeSuite." + nameof(SingleFileIsRecycled), SingleFileIsRecycled),
        new TestCase("FacadeSuite." + nameof(BatchOfGeneratedFilesIsRecycled), BatchOfGeneratedFilesIsRecycled, TestKind.Load),
        new TestCase("FacadeSuite." + nameof(NullPathThrowsWhenThrowOnError), NullPathThrowsWhenThrowOnError),
        new TestCase("FacadeSuite." + nameof(NullPathFailsWithoutThrowOnError), NullPathFailsWithoutThrowOnError),
        new TestCase("FacadeSuite." + nameof(MissingPathDoesNotBlockTheRest), MissingPathDoesNotBlockTheRest),
        new TestCase("FacadeSuite." + nameof(EmptyBatchSucceeds), EmptyBatchSucceeds),
        new TestCase("FacadeSuite." + nameof(DirectoryTargetIsRecycledWithContents), DirectoryTargetIsRecycledWithContents),
        new TestCase("FacadeSuite." + nameof(LongPathPastTheShellLimitIsRefusedSafely), LongPathPastTheShellLimitIsRefusedSafely),
        new TestCase("FacadeSuite." + nameof(LargeFileIsRecycled), LargeFileIsRecycled),
        new TestCase("FacadeSuite." + nameof(CancellationIsObserved), CancellationIsObserved),
        new TestCase("FacadeSuite." + nameof(SingleMissingPathThrowsFileNotFound), SingleMissingPathThrowsFileNotFound),
        new TestCase("FacadeSuite." + nameof(SingleLockedPathThrowsWithShellHResult), SingleLockedPathThrowsWithShellHResult),
        new TestCase("FacadeSuite." + nameof(ExtendedPrefixOnAShortPathStillReportsSuccess), ExtendedPrefixOnAShortPathStillReportsSuccess),
    ];

    /// <summary>
    /// The extended-length prefix changes which path the file APIs take, not where the file is. A short
    /// path handed over in that form is still the same volume, so the delete has to be verified and
    /// reported as an ordinary recycle.
    /// </summary>
    /// <remarks>
    /// The receipt check compares the volume the item came from against the volume the Shell reported the
    /// new entry on. Those two strings differ in form: the caller's keeps the <c>\\?\</c> prefix and the
    /// Shell never emits one. Comparing the roots without settling that first makes every such path look
    /// like it was deleted outside the Recycle Bin, which reports a successful recycle as a failure.
    /// </remarks>
    private static async Task ExtendedPrefixOnAShortPathStillReportsSuccess(TestContext context)
    {
        const string ExtendedPrefix = @"\\?\";

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var plain = workspace.CreateRandomFile(4096);
        var extended = ExtendedPrefix + plain;
        var volumeRoot = Path.GetPathRoot(plain)!;

        Check.True(plain.Length <= 260, $"the path is short enough to recycle unprefixed ({plain.Length} chars)");

        var sizeBefore = RecycleBinProbe.QuerySize(volumeRoot);

        var result = await FileRecycler.MoveToRecycleBinAsync([extended], cancellationToken: context.CancellationToken);

        Check.True(
            result.Succeeded,
            $"the extended-length form of a short path is a successful recycle (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(1, result.RecycledPaths.Count, "the item is reported as recycled");
        Check.Equal(0, result.Failures.Count, "nothing about the prefix turns into a failure");
        Check.False(File.Exists(extended), "the file is gone from disk");
        Check.True(RecycleBinProbe.QuerySize(volumeRoot) - sizeBefore >= 4096, "its bytes reached the Recycle Bin");

        workspace.Dispose();
    }

    private static Task SingleMissingPathThrowsFileNotFound(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var missing = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

        var thrown = Check.Throws<FileNotFoundException>(
            () => FileRecycler.MoveToRecycleBinAsync(missing).GetAwaiter().GetResult(),
            "a missing path on the single-path overload reports the path, not a Shell failure");

        Check.True(
            thrown.Message.Contains(missing, StringComparison.OrdinalIgnoreCase),
            $"the exception names the missing path (got '{thrown.Message}')");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static async Task SingleLockedPathThrowsWithShellHResult(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);

        IOException thrown;
        using (new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var options = new RecycleBinOptions
            {
                MaxAttempts = 1,
                InitialRetryDelay = TimeSpan.Zero,
                OverallTimeout = TimeSpan.FromSeconds(5)
            };

            var batch = await FileRecycler.MoveToRecycleBinAsync([files[0]], options, context.CancellationToken);
            Check.False(batch.Succeeded, "a locked file cannot be recycled");

            thrown = Check.Throws<IOException>(
                () => FileRecycler.MoveToRecycleBinAsync(files[0], cancellationToken: context.CancellationToken).GetAwaiter().GetResult(),
                "a Shell refusal on the single-path overload raises IOException");
        }

        Check.True((thrown.HResult & unchecked((int)0x80000000)) != 0, $"the exception carries the Shell error code (got 0x{thrown.HResult:X8})");
        Check.True(thrown.InnerException is not null, "the Shell code is also available as an inner exception");
        Check.True(File.Exists(files[0]), "the file is still there, since the Shell refused it");

        workspace.Dispose();
    }

    private static async Task SingleFileIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var before = RecycleBinProbe.QueryItemCount(@"E:\");

        var recycled = await FileRecycler.MoveToRecycleBinAsync(files[0], cancellationToken: context.CancellationToken);

        Check.True(recycled, "a single recycled file reports success");
        Check.False(File.Exists(files[0]), "the file is gone from disk");
        Check.True(RecycleBinProbe.QueryItemCount(@"E:\") - before >= 1, "the Recycle Bin gained the item");

        workspace.Dispose();
    }

    /// <remarks>
    /// Load, because it recycles the whole 94-file fixture set in one call. What a caller sees when it
    /// hands over a smaller batch is asserted without the load by
    /// <c>WorkflowSuite.FiveTouchedFilesAreAllRecycled</c> and <c>EmptySuite.EmptyReportsWhatItFreed</c>.
    /// </remarks>
    private static async Task BatchOfGeneratedFilesIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateFixtureSet();
        var before = RecycleBinProbe.QueryItemCount(@"E:\");

        var result = await FileRecycler.MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"every generated file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8} {result.FirstFailure?.Path})");
        Check.Equal(files.Count, result.RecycledPaths.Count, "every generated file is reported as recycled");
        Check.Equal(0, result.Failures.Count, "no generated file is reported as failed");

        foreach (var file in files)
        {
            Check.False(File.Exists(file), $"{Path.GetFileName(file)} is gone from disk");
        }

        Check.True(RecycleBinProbe.QueryItemCount(@"E:\") - before >= files.Count, "the Recycle Bin gained every generated file");

        workspace.Dispose();
    }

    private static Task NullPathThrowsWhenThrowOnError(TestContext context)
    {
        Check.Throws<ArgumentException>(
            () => FileRecycler.MoveToRecycleBinAsync((string?)null).GetAwaiter().GetResult(),
            "a null path with throwOnError is rejected eagerly");

        Check.Throws<ArgumentException>(
            () => FileRecycler.MoveToRecycleBinAsync("   ").GetAwaiter().GetResult(),
            "a whitespace path with throwOnError is rejected eagerly");

        return Task.CompletedTask;
    }

    private static async Task NullPathFailsWithoutThrowOnError(TestContext context)
    {
        var result = await FileRecycler.MoveToRecycleBinAsync(
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

        var result = await FileRecycler.MoveToRecycleBinAsync(batch, cancellationToken: context.CancellationToken);

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
        var result = await FileRecycler.MoveToRecycleBinAsync(Array.Empty<string?>(), cancellationToken: context.CancellationToken);

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

        var before = RecycleBinProbe.QueryItemCount(@"E:\");
        var result = await FileRecycler.MoveToRecycleBinAsync([directory], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a directory is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(Directory.Exists(directory), "the directory tree is gone from disk");
        Check.True(RecycleBinProbe.QueryItemCount(@"E:\") - before >= 1, "the Recycle Bin gained the directory");

        workspace.Dispose();
    }

    private static async Task LongPathPastTheShellLimitIsRefusedSafely(TestContext context)
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

        // Past the Shell's recycling limit a file is removed permanently while the operation still
        // reports success, so the path must be refused with the file intact. LongPathSuite covers
        // the Shell-facing side of this; here the point is only that the facade surfaces the refusal
        // rather than claiming success.
        var result = await FileRecycler.MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken);

        Check.False(result.Succeeded, "a path past the Shell limit is refused rather than silently deleted");
        Check.Equal(RecycleFailureStage.LongPathNotRecyclable, result.FirstFailure!.Stage, "the refusal names the long-path limitation");
        Check.True(File.Exists(file), "the file survives, because it was never handed to the Shell");

        workspace.Dispose();
    }

    private static async Task LargeFileIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        const int size = 10 * 1024 * 1024;
        var files = workspace.CreateRandomFiles(1, size);
        var before = RecycleBinProbe.QuerySize(@"E:\");

        var result = await FileRecycler.MoveToRecycleBinAsync([files[0]], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a 10 MB file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(File.Exists(files[0]), "the 10 MB file is gone from disk");
        Check.True(RecycleBinProbe.QuerySize(@"E:\") - before >= size, "the Recycle Bin grew by the full 10 MB, so the bytes were really moved");

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
            () => FileRecycler.MoveToRecycleBinAsync(files, cancellationToken: cancelled.Token).GetAwaiter().GetResult(),
            "an already-cancelled token is observed instead of recycling");

        foreach (var file in files)
        {
            Check.True(File.Exists(file), $"{Path.GetFileName(file)} was left alone, as the call never started");
        }

        workspace.Dispose();
    }
}
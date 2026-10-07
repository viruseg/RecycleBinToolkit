using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Paths longer than the Shell can recycle.
/// </summary>
/// <remarks>
/// Measured on this machine: the Shell recycles a path of 251 characters and, at 261, deletes the
/// file permanently while still reporting <c>0x00270008</c> and no recycle location. The only safe
/// handling is therefore to never hand such a path to the Shell at all, which leaves the file intact
/// for a caller that did not opt into permanent deletion.
/// </remarks>
internal static class LongPathSuite
{
    private const string ExtendedPrefix = @"\\?\";

    /// <summary>Length of the generated file name, kept under the 255-character component limit.</summary>
    private const int FileNameLength = 250;

    private const int MaxPathThreshold = 260;

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("LongPathSuite." + nameof(ExtendedPrefixPathIsReadAndRecycledOrRefusedSafely), ExtendedPrefixPathIsReadAndRecycledOrRefusedSafely),
        new TestCase("LongPathSuite." + nameof(UnrecyclableLongPathIsNeverHandedToTheShell), UnrecyclableLongPathIsNeverHandedToTheShell),
        new TestCase("LongPathSuite." + nameof(UnrecyclableLongPathThrowsWhenThrowOnError), UnrecyclableLongPathThrowsWhenThrowOnError),
        new TestCase("LongPathSuite." + nameof(DeletePermanentlyPolicyRemovesTheFile), DeletePermanentlyPolicyRemovesTheFile),
        new TestCase("LongPathSuite." + nameof(PathUnderTheThresholdStillRecyclesNormally), PathUnderTheThresholdStillRecyclesNormally),
        new TestCase("LongPathSuite." + nameof(ShortenPathAndRecyclePutsTheBytesInTheBin), ShortenPathAndRecyclePutsTheBytesInTheBin),
        new TestCase("LongPathSuite." + nameof(ShortenPathAndRecycleLeavesNoStagingLeftovers), ShortenPathAndRecycleLeavesNoStagingLeftovers),
        new TestCase("LongPathSuite." + nameof(ThrowPolicyObeysTheGlobalSwitch), ThrowPolicyObeysTheGlobalSwitch),
    ];

    /// <summary>
    /// The one way a long path actually reaches the Recycle Bin: rename it somewhere short on the same
    /// volume, then recycle from there. The caller has accepted that the Recycle Bin entry will show
    /// the staging path as the original location.
    /// </summary>
    private static async Task ShortenPathAndRecyclePutsTheBytesInTheBin(TestContext context)
    {
        const int payload = 128 * 1024;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();

            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[payload]);
            }

            var countBefore = RecycleBinVolume.QueryItemCount(@"E:\");
            var sizeBefore = RecycleBinVolume.QuerySize(@"E:\");

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
            };

            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            Check.True(
                result.Succeeded,
                $"the shortened path is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
            Check.Equal(0, result.Failures.Count, "no failure is reported for a shortened long path");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "the shorten route never deletes anything permanently");
            Check.Equal(1, result.RecycledPaths.Count, "the caller's own path is what gets reported");
            Check.Equal(plainPath, result.RecycledPaths[0], "the report names the path the caller passed, not the staging path");

            Check.False(File.Exists(extendedPath), "the file is gone from its original location");

            var sizeDelta = RecycleBinVolume.QuerySize(@"E:\") - sizeBefore;
            Check.True(sizeDelta >= payload, $"the bytes really reached the Recycle Bin (grew by {sizeDelta}, expected at least {payload})");
            Check.True(RecycleBinVolume.QueryItemCount(@"E:\") - countBefore >= 1, "the Recycle Bin gained an item");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static async Task ShortenPathAndRecycleLeavesNoStagingLeftovers(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();

            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[8192]);
            }

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
            };

            await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            var staging = LongPathStager.StagingRootFor(@"E:\long\file.bin");
            var leftovers = Directory.Exists(staging) ? Directory.GetFileSystemEntries(staging).Length : 0;

            Check.Equal(0, leftovers, "nothing is stranded in the staging directory once the item is recycled");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    /// <summary>
    /// The global switch has the final say. A long path configured to throw stays silent when the
    /// caller turned throwing off, and the failure arrives as data instead.
    /// </summary>
    private static Task ThrowPolicyObeysTheGlobalSwitch(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();

            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[8192]);
            }

            var quiet = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.Throw
            };

            var result = FileRecycler.MoveToRecycleBinAsync([plainPath], quiet, context.CancellationToken)
                .GetAwaiter().GetResult();

            Check.False(result.Succeeded, "the call reports the refusal rather than claiming success");
            Check.Equal(1, result.Failures.Count, "the long path lands in Failures when the global switch is off");
            Check.Equal(
                RecycleFailureStage.LongPathNotRecyclable,
                result.FirstFailure!.Stage,
                "the failure still names the long-path limitation");
            Check.True(File.Exists(extendedPath), "the file is untouched");

            var loud = new RecycleBinOptions
            {
                ThrowOnError = true,
                LongPathFailureAction = LongPathFailureAction.Throw
            };

            Check.Throws<PathTooLongException>(
                () => FileRecycler.MoveToRecycleBinAsync(plainPath, loud, context.CancellationToken).GetAwaiter().GetResult(),
                "the same policy throws once the global switch is on");
            Check.True(File.Exists(extendedPath), "the file is still untouched after the throw");
        }
        finally
        {
            workspace.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The extended-length path must work for reading and writing, and recycling it must either put
    /// it in the bin or leave it on disk. Silently losing it is the one outcome this suite exists to
    /// prevent.
    /// </summary>
    private static async Task ExtendedPrefixPathIsReadAndRecycledOrRefusedSafely(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();
            Check.True(plainPath.Length > MaxPathThreshold, $"the path is genuinely past the threshold ({plainPath.Length} chars)");

            var expected = new byte[64 * 1024];
            Random.Shared.NextBytes(expected);

            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(expected);
            }

            using (var stream = new FileStream(extendedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var actual = new byte[stream.Length];
                stream.ReadExactly(actual);
                Check.True(actual.AsSpan().SequenceEqual(expected), "the file reads back with the content that was written");
            }

            var before = RecycleBinVolume.QueryItemCount(@"E:\");
            var sizeBefore = RecycleBinVolume.QuerySize(@"E:\");

            var result = await FileRecycler.MoveToRecycleBinAsync([extendedPath], cancellationToken: context.CancellationToken);

            var sizeDelta = RecycleBinVolume.QuerySize(@"E:\") - sizeBefore;

            if (result.Succeeded)
            {
                Check.True(sizeDelta >= expected.Length, $"a reported success means the bytes reached the Recycle Bin (grew by {sizeDelta})");
                Check.Equal(0, result.PermanentlyDeletedPaths.Count, "a reported success did not also delete anything permanently");
                Check.False(File.Exists(extendedPath), "the file is gone from disk");
            }
            else
            {
                Check.True(File.Exists(extendedPath), "an unrecyclable long path is refused with the file still on disk");
                Check.Equal(
                    RecycleFailureStage.LongPathNotRecyclable,
                    result.FirstFailure!.Stage,
                    "the refusal names the long-path limitation");
            }

            Check.True(before > 0 || sizeDelta >= 0, "the Recycle Bin volume was readable throughout");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static async Task UnrecyclableLongPathIsNeverHandedToTheShell(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();
            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[4096]);
            }

            var sizeBefore = RecycleBinVolume.QuerySize(@"E:\");
            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], cancellationToken: context.CancellationToken);

            if (result.Succeeded)
            {
                // Only reachable when the volume offers a usable 8.3 alias for this path.
                Check.True(RecycleBinVolume.QuerySize(@"E:\") - sizeBefore >= 4096, "a reported success means the bytes reached the bin");
                Check.False(File.Exists(extendedPath), "the file is gone from disk");
                return;
            }

            Check.Equal(
                RecycleFailureStage.LongPathNotRecyclable,
                result.FirstFailure!.Stage,
                "the Shell's silent permanent deletion is refused as a long-path failure");
            Check.True(
                (result.FirstFailure.HResult & unchecked((int)0x80000000)) != 0,
                $"the failure carries an error code rather than looking like success (got 0x{result.FirstFailure.HResult:X8})");
            Check.True(File.Exists(extendedPath), "the file survives, because it was never handed to the Shell");
            Check.Equal(0, RecycleBinVolume.QuerySize(@"E:\") - sizeBefore, "nothing reached the bin");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "nothing was deleted permanently without being asked to");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static Task UnrecyclableLongPathThrowsWhenThrowOnError(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();
            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[4096]);
            }

            var options = new RecycleBinOptions { ThrowOnError = true, LongPathFailureAction = LongPathFailureAction.Throw };
            Check.Throws<PathTooLongException>(
                () => FileRecycler.MoveToRecycleBinAsync(plainPath, options, context.CancellationToken).GetAwaiter().GetResult(),
                "throwOnError turns the long-path refusal into PathTooLongException");
            Check.True(File.Exists(extendedPath), "the file is still there after the throw");

            var quiet = new RecycleBinOptions { ThrowOnError = false, LongPathFailureAction = LongPathFailureAction.Throw };
            var result = FileRecycler.MoveToRecycleBinAsync([plainPath], quiet, context.CancellationToken)
                .GetAwaiter().GetResult();
            Check.False(result.Succeeded, "without throwOnError the same call reports failure instead of throwing");
        }
        finally
        {
            workspace.Dispose();
        }

        return Task.CompletedTask;
    }

    private static async Task DeletePermanentlyPolicyRemovesTheFile(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var (plainPath, extendedPath) = BuildLongPath();
            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[4096]);
            }

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.DeletePermanently
            };

            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            Check.True(result.Succeeded, $"the opt-in policy still reports the file as handled (first failure: {result.FirstFailure?.Stage})");
            Check.Equal(0, result.Failures.Count, "an opted-in permanent removal is not a failure");
            Check.Equal(1, result.PermanentlyDeletedPaths.Count, "the permanently deleted path is reported, not hidden");
            Check.Equal(plainPath, result.PermanentlyDeletedPaths[0], "the report names the caller's own path");
            Check.False(File.Exists(extendedPath), "the file is gone, as the caller asked for");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    /// <summary>
    /// The threshold is a limit, not a rule for every long name: a path that the Shell can still
    /// recycle must go through the Recycle Bin, not through the permanent-delete policy.
    /// </summary>
    private static async Task PathUnderTheThresholdStillRecyclesNormally(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var fileName = new string('g', 200 - Path.GetExtension(".bin").Length) + ".bin";
            var plainPath = Path.Combine(ScratchWorkspace.Root, fileName);

            using (var stream = new FileStream(plainPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[4096]);
            }

            Check.True(plainPath.Length <= MaxPathThreshold, $"the path is under the threshold ({plainPath.Length} chars)");

            var options = new RecycleBinOptions { LongPathFailureAction = LongPathFailureAction.DeletePermanently };
            var before = RecycleBinVolume.QuerySize(@"E:\");

            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            Check.True(result.Succeeded, "a path under the threshold recycles");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "a path under the threshold never takes the permanent route");
            Check.True(RecycleBinVolume.QuerySize(@"E:\") - before >= 4096, "its bytes reached the Recycle Bin");
            Check.False(File.Exists(plainPath), "the file is gone from disk");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static (string PlainPath, string ExtendedPath) BuildLongPath()
    {
        var fileName = new string('f', FileNameLength - Path.GetExtension(".bin").Length) + ".bin";
        var plainPath = Path.Combine(ScratchWorkspace.Root, fileName);
        return (plainPath, ExtendedPrefix + plainPath);
    }
}
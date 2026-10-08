using System.Collections;
using System.Diagnostics;
using System.Reflection;
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
        new TestCase("LongPathSuite." + nameof(DeletePermanentlyReportsEveryLongPathItHandled), DeletePermanentlyReportsEveryLongPathItHandled),
        new TestCase("LongPathSuite." + nameof(PathUnderTheThresholdStillRecyclesNormally), PathUnderTheThresholdStillRecyclesNormally),
        new TestCase("LongPathSuite." + nameof(ShortenPathAndRecyclePutsTheBytesInTheBin), ShortenPathAndRecyclePutsTheBytesInTheBin),
        new TestCase("LongPathSuite." + nameof(ShortenPathAndRecycleRemovesTheStagingDirectory), ShortenPathAndRecycleRemovesTheStagingDirectory),
        new TestCase("LongPathSuite." + nameof(ThrowPolicyObeysTheGlobalSwitch), ThrowPolicyObeysTheGlobalSwitch),
        new TestCase("LongPathSuite." + nameof(StagingDirectoryNamesAreUniqueAndShortEnough), StagingDirectoryNamesAreUniqueAndShortEnough),
        new TestCase("LongPathSuite." + nameof(StagingDirectoryIsRemovedAfterTheOperation), StagingDirectoryIsRemovedAfterTheOperation),
        new TestCase("LongPathSuite." + nameof(StagingKeepsAnythingItCouldNotRecover), StagingKeepsAnythingItCouldNotRecover),
        new TestCase("LongPathSuite." + nameof(PermanentDeleteIsStillReportedAlongsideAValidationFailure), PermanentDeleteIsStillReportedAlongsideAValidationFailure),
        new TestCase("LongPathSuite." + nameof(LockedLongPathFailsTheBatchInsteadOfThrowing), LockedLongPathFailsTheBatchInsteadOfThrowing),
        new TestCase("LongPathSuite." + nameof(AContendedBatchIsNotRetriedOnePathAtATime), AContendedBatchIsNotRetriedOnePathAtATime),
        new TestCase("LongPathSuite." + nameof(StagedItemThatCouldNotBeRecycledIsPutBack), StagedItemThatCouldNotBeRecycledIsPutBack),
        new TestCase("LongPathSuite." + nameof(DeletePermanentlyRemovesALongDirectoryToo), DeletePermanentlyRemovesALongDirectoryToo),
        new TestCase("LongPathSuite." + nameof(AFaultAfterStagingStillPutsTheItemBack), AFaultAfterStagingStillPutsTheItemBack),
    ];

    /// <summary>
    /// Staging happens before the retry loop, so an operation that faults after that point has already
    /// moved the caller's file and never got to do anything useful with it. The move back has to happen
    /// on that path too, or the bytes are left in a staging directory the caller was told nothing about.
    /// </summary>
    /// <remarks>
    /// The runner is driven directly rather than through <see cref="FileRecycler"/>, because that is the
    /// only way to place a fault after staging. <c>SubmitAsync</c> checks the token before the batch is
    /// ever queued, so a cancelled call through the public API never starts staging and cannot show this.
    /// Here the token is already cancelled when the runner is entered, so the batch is staged and then
    /// throws on the loop's first cancellation check — after the file has left its original location.
    /// </remarks>
    private static Task AFaultAfterStagingStillPutsTheItemBack(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var (plainPath, extendedPath) = BuildLongPath('z');

        File.WriteAllBytes(extendedPath, new byte[4096]);

        // Same reason the backoff case skips: where the volume issues 8.3 names the path is recycled
        // directly and never staged, so there would be nothing here to put back.
        if (ShortPathResolver.TryResolveShort(plainPath) is not null)
        {
            Console.WriteLine("        skipped: this volume issues 8.3 names, so long paths never reach staging");
            workspace.Dispose();
            return Task.CompletedTask;
        }

        var options = new RecycleBinOptions
        {
            ThrowOnError = false,
            LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
        };

        var targets = new List<RecycleTarget>
        {
            new(plainPath, extendedPath, RecycleTarget.StripExtendedPrefix(extendedPath))
        };

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Check.Throws<OperationCanceledException>(
            () => StaThread.Run(() =>
            {
                using var shell = ShellOperationContext.Create();
                return new RecycleOperationRunner(shell).Run(targets, options, cancelled.Token);
            }),
            "the runner reports the fault rather than swallowing it");

        Check.True(
            File.Exists(extendedPath),
            "and the item it had already moved aside is back where the caller had it");

        Check.Equal(
            0,
            CountStagingDirectories(),
            "and the operation's own scratch directory is gone, because the item was put back out of it");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The direct-removal policy answers the same way for a directory as for a file. Removing a file
    /// whose path is too long is a plain unlink, and nothing about a directory makes that harder — the
    /// path length is what brought it here, not what it points at.
    /// </summary>
    /// <remarks>
    /// File.Delete cannot remove a directory at all: it raises UnauthorizedAccessException, which reads
    /// as "you do not have permission" and sends the caller looking for an access problem that does not
    /// exist. The batch still has to come back clean, and the directory still has to be gone.
    /// </remarks>
    private static Task DeletePermanentlyRemovesALongDirectoryToo(TestContext context)
    {
        var root = ScratchWorkspace.RecreateLongDirectoryRoot();

        // Nested rather than one long component: a directory name cannot exceed 255 characters, so a
        // directory past the Shell's limit is a chain of them.
        var longPath = root;
        for (var i = 0; i < 4; i++)
        {
            longPath = Path.Combine(longPath, new string('d', 60) + i);
        }

        longPath = Path.Combine(longPath, new string('d', 60) + "9");

        Check.True(longPath.Length > MaxPathThreshold, $"the directory path is past the Shell limit ({longPath.Length})");

        Directory.CreateDirectory(ExtendedPrefix + longPath);
        File.WriteAllBytes(Path.Combine(ExtendedPrefix + longPath, "inner.bin"), new byte[4096]);

        var options = new RecycleBinOptions
        {
            ThrowOnError = false,
            LongPathFailureAction = LongPathFailureAction.DeletePermanently
        };

        var result = FileRecycler.MoveToRecycleBinAsync([longPath], options, context.CancellationToken)
            .GetAwaiter().GetResult();

        Check.True(
            result.Succeeded,
            $"a long directory the Shell cannot recycle is removed outright (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(1, result.PermanentlyDeletedPaths.Count, "and it is reported as removed");
        Check.Equal(longPath, result.PermanentlyDeletedPaths[0], "by the path the caller passed");
        Check.False(Directory.Exists(longPath), "the directory is gone");

        return Task.CompletedTask;
    }

    /// <summary>
    /// An item moved aside to reach the Shell and then failed to recycle has to go back where the
    /// caller left it. That is the whole promise of staging a rename: nothing is copied, and a failure
    /// is supposed to leave the caller's tree as it was found.
    /// </summary>
    /// <remarks>
    /// The restore is reached directly rather than by provoking the Shell into refusing a staged item,
    /// because almost everything that makes the Shell refuse a delete — an open handle without delete
    /// sharing, a locked child directory — also blocks the rename that staging is, so the item would
    /// never reach the staging step the assertion is about. What is asserted here is the observable
    /// outcome the caller depends on: the bytes are back at the path they were handed.
    /// <para>
    /// The runner keeps its per-item bookkeeping private, so the structures are built by reflection and
    /// the real <c>RestoreUnrecycledStagedItems</c> is invoked rather than a restatement of it. That
    /// way this test fails if the restore regresses, whatever shape the bookkeeping takes.
    /// </para>
    /// </remarks>
    private static Task StagedItemThatCouldNotBeRecycledIsPutBack(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var original = workspace.CreateRandomFile(4096);

        var stager = new LongPathStager();
        var staged = stager.TryStageOnce(original);

        try
        {
            Check.NotNull(staged, $"the item could be staged ({staged})");
            Check.False(File.Exists(original), "staging moved the item out of the caller's location");
            Check.True(File.Exists(staged!), "and the staging copy holds the bytes");

            RestoreStagedItem(staged!, original);

            Check.True(
                File.Exists(original),
                "an item that could not be recycled is put back where the caller had it");
            Check.False(File.Exists(staged!), "and its staging copy is gone, because the move went through");
        }
        finally
        {
            stager.Dispose();
            workspace.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Invokes the runner's private restore with one staged entry that was not among the recycled
    /// paths, which is the state a failed recycle leaves behind.
    /// </summary>
    private static void RestoreStagedItem(string stagedPath, string originalPath)
    {
        var runnerType = typeof(RecycleOperationRunner);
        var stagedType = runnerType.Assembly.GetType(
            "RecycleBinToolkit.RecycleOperationRunner+StagedTarget",
            throwOnError: true)!;

        // The same two steps the runner performs when it stages: the target is told where the item
        // actually is, and the entry carries where it came from.
        var target = new RecycleTarget(originalPath, originalPath, originalPath);
        var moved = typeof(RecycleTarget)
            .GetMethod("WithMovedTo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, [stagedPath])!;

        var entry = stagedType
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(c => c.GetParameters().Length == 2)
            .Invoke([moved, originalPath]);

        var staged = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stagedType))!;
        staged.Add(entry);

        var restore = runnerType.GetMethod(
            "RestoreUnrecycledStagedItems",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        // Nothing was recycled, so the recycled-paths list is empty: every staged entry is unrestored work.
        restore.Invoke(null, [staged, new List<string>()]);
    }

    /// <summary>
    /// The wait between staging retries must be paid once for the batch, not once per path.
    /// </summary>
    /// <remarks>
    /// Staging is a rename, and a rename can lose a race with the indexer or antivirus, so it is
    /// retried. Doing that per path means a batch of contended paths spends the whole backoff sequence
    /// on each one in turn — and the Shell worker is a single thread that everything else queues behind,
    /// so that is dead time for every other caller in the process. Trying each path once first and only
    /// then retrying what failed collapses it to one sequence.
    /// <para>
    /// The bound is loose on purpose: serial retry of six paths needs at least six times the backoff,
    /// batched needs about one, and anything in between would mean the two are not as different as the
    /// shape of the loop suggests.
    /// </para>
    /// </remarks>
    private static async Task AContendedBatchIsNotRetriedOnePathAtATime(TestContext context)
    {
        const int pathCount = 6;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var held = new List<FileStream>(pathCount);

        try
        {
            var paths = new List<string>(pathCount);

            for (var i = 0; i < pathCount; i++)
            {
                var extendedPath = BuildLongPath((char)('a' + i)).ExtendedPath;
                File.WriteAllBytes(extendedPath, new byte[4096]);

                held.Add(new FileStream(extendedPath, FileMode.Open, FileAccess.Read, FileShare.Read));
                paths.Add(ScratchWorkspace.Root + Path.GetFileName(extendedPath));
            }

            // This is a test about the staging backoff, so the paths have to actually reach the staging
            // code. Where the volume hands out 8.3 aliases they would be recycled directly instead, the
            // run would measure the Shell's own retry policy, and the bound below would say nothing about
            // the thing it names. Skipping is the honest outcome on such a volume, not a pass.
            if (ShortPathResolver.TryResolveShort(paths[0]) is not null)
            {
                Console.WriteLine("        skipped: this volume issues 8.3 names, so long paths never reach the staging backoff");
                return;
            }

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
            };

            var stopwatch = Stopwatch.StartNew();
            var result = await FileRecycler.MoveToRecycleBinAsync(paths, options, context.CancellationToken);
            stopwatch.Stop();

            Check.False(result.Succeeded, "every path is held, so none of them can be staged");
            Check.Equal(pathCount, result.Failures.Count, "and every one of them is reported");

            const int SerialRetryFloorMs = 900;
            Check.True(
                stopwatch.ElapsedMilliseconds < SerialRetryFloorMs,
                $"{pathCount} contended paths are retried as a batch, not one after another (took {stopwatch.ElapsedMilliseconds} ms, " +
                $"a per-path retry sequence for {pathCount} paths would not fit under {SerialRetryFloorMs} ms)");
        }
        finally
        {
            foreach (var stream in held)
            {
                stream.Dispose();
            }

            workspace.Dispose();
        }
    }

    /// <summary>
    /// A per-item failure is data, never an exception. One locked file in a batch has to cost that file
    /// its recycle and nothing else, which is the whole reason the runner retries only what failed.
    /// </summary>
    /// <remarks>
    /// Removing a long path for good means calling into the file system directly, and that call fails on
    /// its own terms: a handle held without delete sharing, a read-only attribute, a scan in progress.
    /// Those have to land in <see cref="RecycleBinResult.Failures"/> with the code the file system gave,
    /// rather than escaping and taking the rest of the batch with them.
    /// </remarks>
    private static async Task LockedLongPathFailsTheBatchInsteadOfThrowing(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var extendedPath = BuildLongPath('k').ExtendedPath;
            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[4096]);
            }

            var longPath = ScratchWorkspace.Root + Path.GetFileName(extendedPath);

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.DeletePermanently
            };

            // Held open without delete sharing for the whole call, so the direct removal has to fail.
            using (new FileStream(extendedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = await FileRecycler.MoveToRecycleBinAsync([longPath], options, context.CancellationToken);

                Check.False(result.Succeeded, "a long path that cannot be removed for good is a failure");
                Check.Equal(1, result.Failures.Count, "exactly one failure, for the one path that could not be handled");
                Check.Equal(
                    RecycleFailureStage.PermanentDeleteFailed,
                    result.FirstFailure!.Stage,
                    "the stage says the direct removal failed, not that the path was too long");
                Check.True(
                    (result.FirstFailure.HResult & unchecked((int)0x80000000)) != 0,
                    $"the failure carries the file system's own error code (got 0x{result.FirstFailure.HResult:X8})");
                Check.Equal(0, result.PermanentlyDeletedPaths.Count, "nothing is reported as removed that was not removed");
                Check.True(File.Exists(ExtendedPrefix + longPath), "the file is still there, because nothing deleted it");
            }
        }
        finally
        {
            workspace.Dispose();
        }
    }

    /// <summary>
    /// A batch that also carries a path validation failure comes back rebuilt, and the rebuild has to
    /// carry everything the operation learned. Dropping the permanent-delete record is the worst of the
    /// possibilities: the file is gone for good and the caller is never told.
    /// </summary>
    private static async Task PermanentDeleteIsStillReportedAlongsideAValidationFailure(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var extendedPath = BuildLongPath('p').ExtendedPath;
            using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new byte[4096]);
            }

            var longPath = ScratchWorkspace.Root + Path.GetFileName(extendedPath);
            var missing = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.DeletePermanently
            };

            var result = await FileRecycler.MoveToRecycleBinAsync([missing, longPath], options, context.CancellationToken);

            Check.False(result.Succeeded, "the missing path still makes the batch report failure");
            Check.Equal(1, result.Failures.Count, "and it is the only failure");
            Check.Equal(
                RecycleFailureStage.PathValidation,
                result.FirstFailure!.Stage,
                "which is the validation failure the rebuild merged in");
            Check.Equal(
                1,
                result.PermanentlyDeletedPaths.Count,
                $"the long path is still named as permanently deleted (got {Describe(result)})");
            Check.Equal(longPath, result.PermanentlyDeletedPaths[0], "and it is the caller's own path");
            Check.False(File.Exists(ExtendedPrefix + longPath), "the long file is gone, as the caller asked for");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static Task StagingDirectoryNamesAreUniqueAndShortEnough(TestContext context)
    {
        const int Samples = 2000;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longest = 0;

        for (var i = 0; i < Samples; i++)
        {
            var name = LongPathStager.NewDirectoryName();
            Check.True(names.Add(name), $"generated name '{name}' is unique across {Samples} samples");
            Check.True(name.StartsWith(LongPathStager.NamePrefix, StringComparison.Ordinal), $"'{name}' carries the prefix so a leftover is recognisable");
            longest = Math.Max(longest, Path.Combine(ScratchWorkspace.VolumeRoot, name).Length);
        }

        Check.True(longest <= 60, $"even the longest staging path leaves room under the Shell limit ({longest} chars)");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The staging directory is the library's own scratch space on the user's volume. It exists only
    /// for the duration of one operation and must be gone afterwards, including its parent volume
    /// root.
    /// </summary>
    private static async Task StagingDirectoryIsRemovedAfterTheOperation(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var before = CountStagingDirectories();

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

            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            Check.True(result.Succeeded, "the shortened path is recycled");
            Check.True(result.LeftoverStagingDirectory is null, $"nothing was left behind (got '{result.LeftoverStagingDirectory}')");
            Check.Equal(before, CountStagingDirectories(), "the operation created no lasting directory on the volume");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    /// <summary>
    /// Cleanup must never destroy something it could not put back. A staging directory that still
    /// holds an item is left in place and reported, so the data stays recoverable.
    /// </summary>
    private static Task StagingKeepsAnythingItCouldNotRecover(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        // The staging directory is created on the volume root, not under the scratch root, so nothing else
        // this run does removes it. Both of these therefore run in the finally: an assertion failing between
        // them would otherwise leave a directory holding the case's own file in the caller's volume root,
        // and the next case to count staging directories would fail on a leftover of this one.
        var stager = new LongPathStager();
        string[] leftover = [];

        try
        {
            var file = workspace.CreateRandomFile(4096);

            var staged = stager.TryStageOnce(file);
            Check.NotNull(staged, $"the item was staged ({staged})");
            Check.False(File.Exists(file), "the item left its original location");

            leftover = [.. stager.Release()];

            Check.Equal(1, leftover.Length, "a staging directory that still holds the item is reported");
            Check.True(Directory.Exists(leftover[0]), "the leftover directory is still there");
            Check.True(File.Exists(staged!), "the item inside it is still there, so nothing was destroyed");

            Check.Equal(0, stager.Release().Count, "releasing again reports nothing left, because nothing was tried");

            return Task.CompletedTask;
        }
        finally
        {
            foreach (var directory in leftover)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort; the assertion above already proved the content survived, so leaving the
                    // directory is a tidiness problem and not a lost one.
                }
            }

            stager.Dispose();
            workspace.Dispose();
        }
    }

    private static int CountStagingDirectories()
    {
        try
        {
            return Directory
                .GetFileSystemEntries(ScratchWorkspace.VolumeRoot)
                .Count(path => Path.GetFileName(path).StartsWith(LongPathStager.NamePrefix, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

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

            var (countBefore, sizeBefore) = RecycleBinProbe.Measure(ScratchWorkspace.VolumeRoot);

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

            var sizeDelta = RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot) - sizeBefore;
            Check.True(sizeDelta >= payload, $"the bytes really reached the Recycle Bin (grew by {sizeDelta}, expected at least {payload})");
            Check.True(RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - countBefore >= 1, "the Recycle Bin gained an item");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static async Task ShortenPathAndRecycleRemovesTheStagingDirectory(TestContext context)
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

            var stagingBefore = CountStagingDirectories();

            await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            var stagingAfter = CountStagingDirectories();

            Check.Equal(stagingBefore, stagingAfter, "the operation left no staging directory of its own on the volume");
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
    /// The permanent-delete policy has to reach every long path in the batch, not just the first one
    /// that happens to be removable.
    /// </summary>
    /// <remarks>
    /// Reporting is what makes the policy auditable: a caller told only about some of the paths it
    /// passed in cannot tell what became of the rest. Both halves are asserted — the batch reports no
    /// failures, and every path is named — because the second alone would also pass if the remaining
    /// files were silently dropped from the result.
    /// </remarks>
    private static async Task DeletePermanentlyReportsEveryLongPathItHandled(TestContext context)
    {
        const int pathCount = 3;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        try
        {
            var paths = new List<string>(pathCount);

            for (var i = 0; i < pathCount; i++)
            {
                var extendedPath = BuildLongPath((char)('f' + i)).ExtendedPath;
                using (var stream = new FileStream(extendedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(new byte[4096]);
                }

                paths.Add(Path.Combine(ScratchWorkspace.Root, Path.GetFileName(extendedPath)));
            }

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                LongPathFailureAction = LongPathFailureAction.DeletePermanently
            };

            var result = await FileRecycler.MoveToRecycleBinAsync(paths, options, context.CancellationToken);

            Check.Equal(0, result.Failures.Count, $"no long path in the batch is left unreported (got {result.Failures.Count}: {Describe(result)})");
            Check.Equal(pathCount, result.PermanentlyDeletedPaths.Count, $"all {pathCount} long paths are named as permanently deleted (got {Describe(result)})");

            foreach (var path in paths)
            {
                Check.True(
                    result.PermanentlyDeletedPaths.Contains(path, StringComparer.OrdinalIgnoreCase),
                    $"the report names '{Path.GetFileName(path)}'");
                Check.False(File.Exists(ExtendedPrefix + path), $"'{Path.GetFileName(path)}' is gone, as the caller asked for");
            }
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static string Describe(RecycleBinResult result) =>
        $"recycled [{string.Join(", ", result.RecycledPaths)}], " +
        $"deleted [{string.Join(", ", result.PermanentlyDeletedPaths)}], " +
        $"failed [{string.Join(", ", result.Failures.Select(failure => failure.Path))}]";

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

            var (before, sizeBefore) = RecycleBinProbe.Measure(ScratchWorkspace.VolumeRoot);

            var result = await FileRecycler.MoveToRecycleBinAsync([extendedPath], cancellationToken: context.CancellationToken);

            var sizeDelta = RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot) - sizeBefore;

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

            var sizeBefore = RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot);
            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], cancellationToken: context.CancellationToken);

            if (result.Succeeded)
            {
                // Only reachable when the volume offers a usable 8.3 alias for this path.
                Check.True(RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot) - sizeBefore >= 4096, "a reported success means the bytes reached the bin");
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
            Check.Equal(0, RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot) - sizeBefore, "nothing reached the bin");
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
            var before = RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot);

            var result = await FileRecycler.MoveToRecycleBinAsync([plainPath], options, context.CancellationToken);

            Check.True(result.Succeeded, "a path under the threshold recycles");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "a path under the threshold never takes the permanent route");
            Check.True(RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot) - before >= 4096, "its bytes reached the Recycle Bin");
            Check.False(File.Exists(plainPath), "the file is gone from disk");
        }
        finally
        {
            workspace.Dispose();
        }
    }

    private static (string PlainPath, string ExtendedPath) BuildLongPath() =>
        BuildLongPath('f');

    private static (string PlainPath, string ExtendedPath) BuildLongPath(char filler)
    {
        var fileName = new string(filler, FileNameLength - Path.GetExtension(".bin").Length) + ".bin";
        var plainPath = Path.Combine(ScratchWorkspace.Root, fileName);
        return (plainPath, ExtendedPrefix + plainPath);
    }
}
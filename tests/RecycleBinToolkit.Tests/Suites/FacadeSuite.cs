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
        new TestCase("FacadeSuite." + nameof(RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff), RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff),
        new TestCase("FacadeSuite." + nameof(RefusedPathRaisesWhenThrowOnErrorIsOn), RefusedPathRaisesWhenThrowOnErrorIsOn),
        new TestCase("FacadeSuite." + nameof(SinglePathOverloadReturnsFalseWhenRefused), SinglePathOverloadReturnsFalseWhenRefused),
        new TestCase("FacadeSuite." + nameof(AMissingPathIsRefusedWithoutAProbe), AMissingPathIsRefusedWithoutAProbe),
        new TestCase("FacadeSuite." + nameof(NoRecycleBinOptionDecidesWhetherTheShellIsAsked), NoRecycleBinOptionDecidesWhetherTheShellIsAsked),
        new TestCase("FacadeSuite." + nameof(OneBadVolumeDoesNotSinkTheBatch), OneBadVolumeDoesNotSinkTheBatch),
        new TestCase("FacadeSuite." + nameof(ARepeatedRefusedPathIsRefusedOnce), ARepeatedRefusedPathIsRefusedOnce),
    ];

    /// <summary>
    /// A volume with no Recycle Bin must not lose the file while the library reports a successful
    /// recycle, so the path is refused before the Shell is asked and the failure carries the Shell's own
    /// error code.
    /// </summary>
    /// <remarks>
    /// The volume is a <c>subst</c> mapping of the scratch root, so this is the whole failure with no
    /// flash drive and no share: the Shell removes a file handed to it on such a volume for good and
    /// still reports success. Nothing here is asserted about the Shell's behaviour on the volume — only
    /// about what this library did with the answer, which is why the file is asserted to be untouched.
    /// <para>
    /// <c>HResult</c> is checked for a severity bit rather than against a literal: the code is the
    /// Shell's, it differs by volume (<c>0x80070003</c> on a substituted drive, <c>0x80004005</c> on the
    /// removable volume measured in the spec), and what matters here is that a code came through
    /// unchanged instead of being normalised to zero.
    /// </para>
    /// </remarks>
    private static async Task RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();

        try
        {
            await SubstitutedVolume.Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var file = CreateOnSubstitutedVolume(letter);

            var options = new RecycleBinOptions { ThrowOnError = false };

            var result = await FileRecycler
                .MoveToRecycleBinAsync([file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.False(result.Succeeded, $"a volume with no Recycle Bin cannot report success (recycled {result.RecycledPaths.Count})");
            Check.Equal(1, result.Failures.Count, "the refused path is the only failure reported");
            Check.Equal(RecycleFailureStage.NoRecycleBinOnVolume, result.FirstFailure!.Stage, "the failure names the volume, not the Shell's doing");
            Check.Equal(file, result.FirstFailure.Path, "the failure names the caller's path");
            Check.Equal(0, result.FirstFailure.Attempts, "nothing was attempted, because nothing was handed over");
            Check.NotEqual(0, result.FirstFailure.HResult, "the Shell's own error code came through rather than a zero");
            Check.True(
                (result.FirstFailure.HResult & unchecked((int)0x80000000)) != 0,
                $"the carried code is a refusal (got 0x{result.FirstFailure.HResult:X8})");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "nothing was removed permanently either");
            Check.Equal(0, result.RecycledPaths.Count, "a refused path is not reported as recycled");
            Check.True(File.Exists(file), "the file is still on disk, which is the whole point");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The same refusal raises under <see cref="RecycleBinOptions.ThrowOnError"/>, carrying the probe's
    /// error code, and the file is still there when the exception surfaces.
    /// </summary>
    /// <remarks>
    /// <see cref="IOException"/> rather than a new exception type: the single-path overload already
    /// documents a refusal as an <see cref="IOException"/> whose <see cref="Exception.HResult"/> carries
    /// the Shell code, and this is one. The file assertion is inside the same call's aftermath, so a
    /// caller that catches the exception and then looks at the path learns the truth.
    /// </remarks>
    private static async Task RefusedPathRaisesWhenThrowOnErrorIsOn(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();

        try
        {
            await SubstitutedVolume.Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var file = CreateOnSubstitutedVolume(letter);

            var thrown = await Check
                .ThrowsAsync<IOException>(
                    () => FileRecycler.MoveToRecycleBinAsync(file, cancellationToken: context.CancellationToken),
                    "a refused volume raises on the single-path overload when ThrowOnError is on")
                .ConfigureAwait(false);

            Check.NotEqual(0, thrown.HResult, "the exception carries a real error code");
            Check.True(
                (thrown.HResult & unchecked((int)0x80000000)) != 0,
                $"the carried code is a refusal (got 0x{thrown.HResult:X8})");
            Check.True(File.Exists(file), "the file survived the exception, because the Shell was never asked");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The single-path overload reports <see langword="false"/> for a refused path, which is what a
    /// caller branching on the return value will act on.
    /// </summary>
    /// <remarks>
    /// A regression here is the quiet kind: returning <see langword="true"/> would tell the caller the
    /// file is gone when it is on disk, and the caller would not look any further.
    /// </remarks>
    private static async Task SinglePathOverloadReturnsFalseWhenRefused(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();

        try
        {
            await SubstitutedVolume.Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var file = CreateOnSubstitutedVolume(letter);

            var options = new RecycleBinOptions { ThrowOnError = false };

            var removed = await FileRecycler.MoveToRecycleBinAsync(file, options, context.CancellationToken).ConfigureAwait(false);

            Check.False(removed, "a refused path reports false rather than throwing");
            Check.True(File.Exists(file), "and the file is still there, so false is the honest answer");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A path that is not there is refused at <see cref="RecycleFailureStage.PathValidation"/> without a
    /// probe, because there is no volume question to ask about a file that does not exist.
    /// </summary>
    /// <remarks>
    /// This is the case that keeps the gate cheap: a batch of missing paths pays one filesystem probe
    /// each and nothing else. It also pins that the refusal is decided on existence rather than on the
    /// volume, so a missing path on a healthy volume and a missing path on a bin-less one are the same
    /// failure.
    /// </remarks>
    private static async Task AMissingPathIsRefusedWithoutAProbe(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();
        var missing = Path.Combine(SubstitutedRoot(letter), "definitely-absent.bin");

        try
        {
            await SubstitutedVolume
                .Substitute(letter, ScratchWorkspace.Root, context.CancellationToken)
                .ConfigureAwait(false);

            var options = new RecycleBinOptions { ThrowOnError = false };

            var result = await FileRecycler
                .MoveToRecycleBinAsync([missing], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.False(result.Succeeded, "a path that is not there cannot succeed");
            Check.Equal(1, result.Failures.Count, "it is reported once");
            Check.Equal(
                RecycleFailureStage.PathValidation,
                result.FirstFailure!.Stage,
                "and it fails at validation, not at the volume that holds it");
            Check.Equal(0, result.FirstFailure.HResult, "no Shell code is involved when the Shell was never asked");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// <see cref="NoRecycleBinAction.AllowIndeterminate"/> is what lets a caller hand the Shell a path on
    /// a volume whose answer was not <see cref="RecycleBinAvailabilityReason.Enabled"/>.
    /// </summary>
    /// <remarks>
    /// This is the only case here that watches a file leave the disk, and deliberately so: the option
    /// says the caller accepts being wrong about the bin, so the Shell is asked anyway and reports its
    /// own success. What the case proves is that the option reaches the pipeline at all — a gate that
    /// refused regardless of the setting would fail it — and that a passing path is reported as recycled.
    /// </remarks>
    private static async Task NoRecycleBinOptionDecidesWhetherTheShellIsAsked(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();

        try
        {
            await SubstitutedVolume.Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var file = CreateOnSubstitutedVolume(letter);

            var options = new RecycleBinOptions
            {
                ThrowOnError = false,
                NoRecycleBinAction = NoRecycleBinAction.AllowIndeterminate,
            };

            var result = await FileRecycler
                .MoveToRecycleBinAsync([file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.True(
                result.Succeeded,
                $"AllowIndeterminate hands the path over and the Shell reports success (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
            Check.Equal(1, result.RecycledPaths.Count, "the path comes back as recycled, because the Shell said so");
            Check.Equal(file, result.RecycledPaths[0], "in the spelling the caller supplied");
            Check.Equal(0, result.Failures.Count, "the volume's answer did not become a failure");
            Check.False(File.Exists(file), "the Shell removed the file, which is the risk the caller accepted");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// One volume in a batch that cannot recycle is not the whole batch's verdict: the paths on healthy
    /// volumes are still recycled.
    /// </summary>
    /// <remarks>
    /// The gate groups a batch by volume root, so this is the case that fails if it inspects only the
    /// first root, and the case that fails if it refuses the batch as a whole. Both are silent failures
    /// of the other kind — a batch that skips the healthy paths, and a batch that gives up on all of them
    /// because of one.
    /// <para>
    /// The healthy path is on the volume hosting <see cref="ScratchWorkspace.Root"/>, which is the same
    /// directory the substituted drive points at, so both paths in this batch resolve to the same file
    /// system and only the spelling of the volume differs. That is what makes it a test of the grouping
    /// rather than of the storage.
    /// </para>
    /// </remarks>
    private static async Task OneBadVolumeDoesNotSinkTheBatch(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();
        var healthyFiles = workspace.CreateRandomFiles(1, 4096);

        try
        {
            await SubstitutedVolume.Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var refusedFile = CreateOnSubstitutedVolume(letter);

            var options = new RecycleBinOptions { ThrowOnError = false };

            var batch = new List<string?> { refusedFile };
            batch.AddRange(healthyFiles);

            var result = await FileRecycler
                .MoveToRecycleBinAsync(batch, options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.False(result.Succeeded, "the refused path still makes the batch report failure");
            Check.Equal(1, result.Failures.Count, "exactly one failure, the one from the volume with no bin");
            Check.Equal(refusedFile, result.FirstFailure!.Path, "and it is the path on that volume");
            Check.Equal(RecycleFailureStage.NoRecycleBinOnVolume, result.FirstFailure.Stage, "at the volume stage");
            Check.Equal(1, result.RecycledPaths.Count, "the path on the healthy volume was still recycled");
            Check.Equal(healthyFiles[0], result.RecycledPaths[0], "and it is the one the caller supplied");
            Check.False(File.Exists(healthyFiles[0]), "so the healthy file is gone from disk");
            Check.True(File.Exists(refusedFile), "while the refused one is untouched");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A path listed twice on a volume that cannot recycle is refused once, which is what the same batch
    /// would have done had it reached the Shell.
    /// </summary>
    /// <remarks>
    /// The gate answers before <see cref="RecycleOperationRunner.CollapseEquivalentPaths"/> gets a chance
    /// to, so without collapsing here a caller who listed one path twice would read two failures for one
    /// file — the outcome <c>RunnerSuite.DuplicatePathsAreNotReportedAsFailures</c> pins as wrong for the
    /// ordinary path. Both spellings stay in the batch on purpose: what is being checked is the gate's
    /// bookkeeping, not the runner's.
    /// </remarks>
    private static async Task ARepeatedRefusedPathIsRefusedOnce(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = SubstitutedVolume.FirstUnusedDriveLetter();

        try
        {
            await SubstitutedVolume.Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var file = CreateOnSubstitutedVolume(letter);

            var options = new RecycleBinOptions { ThrowOnError = false };

            var result = await FileRecycler
                .MoveToRecycleBinAsync([file, file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.False(result.Succeeded, "the refused path still fails the operation");
            Check.Equal(1, result.Failures.Count, "one path is refused once, however many times it was listed");
            Check.Equal(file, result.FirstFailure!.Path, "and the failure names the caller's spelling");
            Check.Equal(0, result.RecycledPaths.Count, "nothing reached the Shell, so nothing was recycled");
            Check.True(File.Exists(file), "the file is untouched");
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The drive letter <paramref name="letter"/> answers as a volume root, with its trailing separator.
    /// </summary>
    private static string SubstitutedRoot(char letter) =>
        letter + ":" + Path.DirectorySeparatorChar;

    /// <summary>
    /// Creates one file on the substituted drive named by <paramref name="letter"/> and returns its path
    /// in that spelling, which is the spelling the caller under test supplies.
    /// </summary>
    /// <remarks>
    /// Depends on the mapping already being in place, so the file is written through the drive letter the
    /// case then hands to the library. Writing it through <c>E:\</c> instead would put it on the healthy
    /// volume and test nothing.
    /// </remarks>
    private static string CreateOnSubstitutedVolume(char letter)
    {
        var root = SubstitutedRoot(letter);
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, $"refused-{Environment.ProcessId}.bin");

        File.WriteAllBytes(file, new byte[4096]);

        return file;
    }

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
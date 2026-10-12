using System.Reflection;
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
        new TestCase("FacadeSuite." + nameof(LongPathRecyclesWithNoStagingNoPolicyNoLimit), LongPathRecyclesWithNoStagingNoPolicyNoLimit),
        new TestCase("FacadeSuite." + nameof(LargeFileIsRecycled), LargeFileIsRecycled),
        new TestCase("FacadeSuite." + nameof(CancellationIsObserved), CancellationIsObserved),
        new TestCase("FacadeSuite." + nameof(CancellationIsObservedWhileABatchIsValidated), CancellationIsObservedWhileABatchIsValidated),
        new TestCase("FacadeSuite." + nameof(SingleMissingPathThrowsFileNotFound), SingleMissingPathThrowsFileNotFound),
        new TestCase("FacadeSuite." + nameof(SingleLockedPathThrowsWithFileSystemHResult), SingleLockedPathThrowsWithFileSystemHResult),
        new TestCase("FacadeSuite." + nameof(ExtendedPrefixOnAShortPathStillReportsSuccess), ExtendedPrefixOnAShortPathStillReportsSuccess),
        new TestCase("FacadeSuite." + nameof(RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff), RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff),
        new TestCase("FacadeSuite." + nameof(RefusedPathRaisesWhenThrowOnErrorIsOn), RefusedPathRaisesWhenThrowOnErrorIsOn),
        new TestCase("FacadeSuite." + nameof(SinglePathOverloadReturnsFalseWhenRefused), SinglePathOverloadReturnsFalseWhenRefused),
        new TestCase("FacadeSuite." + nameof(AMissingPathIsRefusedWithoutAProbe), AMissingPathIsRefusedWithoutAProbe),
        new TestCase("FacadeSuite." + nameof(NoRecycleBinOptionDecidesWhetherThePathIsRecycled), NoRecycleBinOptionDecidesWhetherThePathIsRecycled),
        new TestCase("FacadeSuite." + nameof(OneBadVolumeDoesNotSinkTheBatch), OneBadVolumeDoesNotSinkTheBatch),
        new TestCase("FacadeSuite." + nameof(ARepeatedRefusedPathIsRefusedOnce), ARepeatedRefusedPathIsRefusedOnce),
        new TestCase("FacadeSuite." + nameof(DeletePermanentlyRemovesTheFileAndNamesIt), DeletePermanentlyRemovesTheFileAndNamesIt),
        new TestCase("FacadeSuite." + nameof(DeletePermanentlyRemovesADirectory), DeletePermanentlyRemovesADirectory),
        new TestCase("FacadeSuite." + nameof(DeletePermanentlyReportsAFileSystemRefusal), DeletePermanentlyReportsAFileSystemRefusal),
        new TestCase("FacadeSuite." + nameof(APathIsNeverBothRecycledAndPermanentlyDeleted), APathIsNeverBothRecycledAndPermanentlyDeleted),
        new TestCase("FacadeSuite." + nameof(AVolumeGuidPathIsSettledOnceRatherThanRetried), AVolumeGuidPathIsSettledOnceRatherThanRetried),
    ];

    /// <summary>
    /// A volume named by its GUID has no spelling this library can measure,
    /// empty or report against.
    /// </summary>
    /// <remarks>
    /// Refused at the path rather than settled against the volume, so nothing is spent on it: no volume
    /// inspection, no round. <see cref="RecycleFailure.Attempts"/> is therefore zero, which is what
    /// "nothing was attempted" means.
    /// <para>
    /// The file is created under the run's own directory and only addressed by GUID afterwards, so the
    /// write stays where every other write in this suite stays and the run's teardown removes it. A
    /// volume whose GUID <c>GetVolumeNameForVolumeMountPoint</c> cannot name is a case this cannot set up
    /// at all — there is no spelling of the path the case would be about — and that is a skip rather than a
    /// failure.
    /// </para>
    /// </remarks>
    private static async Task AVolumeGuidPathIsSettledOnceRatherThanRetried(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;
        var guid = RecycleBinVolumeState.TryGetVolumeGuidKeyName(volumeRoot);

        if (guid is null)
        {
            workspace.Dispose();
            Check.Skip($"{volumeRoot} has no volume GUID, so no path on it can be addressed the way this case is about");
        }

        var name = $"addressed-by-guid-{Environment.ProcessId}.bin";
        var file = Path.Combine(ScratchWorkspace.Root, name);
        File.WriteAllBytes(file, new byte[4096]);

        // Addressed by the path the volume spells, not by the leaf: a device path is volume-relative, so
        // the run's own directory has to be part of it or this names a file at the volume root that was
        // never written. Deriving it rather than spelling it keeps the two the same path by construction.
        var relative = Path.GetRelativePath(volumeRoot, file);
        var addressed = $@"\\?\Volume{guid}\{relative}";

        // Asserted against the filesystem rather than against the two locals it was built from: the
        // previous version compared the addressed path with the string it was concatenated from, which
        // holds for any guid and any relative path -- including the volume-root one that named a file
        // that was never written. This asks the only question that has an answer to be wrong about.
        Check.True(
            File.Exists(addressed),
            $"the device path names the file that was written, so refusing it is refusing a real item (got {addressed})");

        Check.Equal(
            new FileInfo(file).Length,
            new FileInfo(addressed).Length,
            "and it is that file, not another one of the same name");

        var result = await RecycleBin
            .RecycleAsync([addressed], RecycleOptions.Default, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.False(result.Succeeded, $"a path nothing above the library can read is not recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(1, result.Failures.Count, "exactly one failure is reported");
        Check.Equal(RecycleFailureStage.VolumeGuidPathNotAddressable, result.FirstFailure!.Stage, "the path itself is what could not be used");
        Check.Equal(0, result.FirstFailure.Attempts, "and it was settled before any round, rather than retried for every attempt the caller asked for");
        Check.Equal(0, result.FirstFailure.HResult, "carrying no code, because nothing was ever asked");
        Check.True(File.Exists(file), "the file is exactly where it was left");

        // The same path written with forward slashes, which Windows and Path.GetFullPath both read as the
        // device form. Checked against the caller's own spelling this is not a volume GUID path at all, so
        // without the second check it is refused as something else and retried once per attempt the caller
        // asked for.
        var slashed = addressed.Replace('\\', '/');

        Check.False(RecycleTarget.IsVolumeGuidPath(slashed), "the caller's own spelling carries no backslashes at all");

        var viaSlashes = await RecycleBin
            .RecycleAsync([slashed], RecycleOptions.Default, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(1, viaSlashes.Failures.Count, $"the forward-slash spelling is refused too (got {viaSlashes.FirstFailure?.Stage})");
        Check.Equal(
            RecycleFailureStage.VolumeGuidPathNotAddressable,
            viaSlashes.FirstFailure!.Stage,
            "and it is the same refusal, which only the check against the normalised path can produce");
        Check.Equal(0, viaSlashes.FirstFailure.Attempts, "with nothing attempted on the way to it");
        Check.True(File.Exists(file), "and the file is still there after being refused twice");

        workspace.Dispose();
    }

    /// <summary>
    /// A volume with no Recycle Bin must not lose the file, so the path is refused before anything is
    /// attempted and the failure carries no code.
    /// </summary>
    /// <remarks>
    /// Nothing here is asserted about the volume beyond what this library did with the answer, which is
    /// why the file is asserted to be untouched.
    /// <para>
    /// The file lives on the volume <c>--scratch</c> named, and this case needs that volume to have no
    /// Recycle Bin, so <see cref="ScratchWorkspace.RequireNonRecycling"/> decides whether it runs at all:
    /// a machine with a bin reports this one as not run rather than failing it.
    /// </para>
    /// <para>
    /// <c>HResult</c> is pinned to <c>0</c>: the refusal is settled from configuration, so there is no
    /// code to carry. What matters is that the volume's own name came through unchanged instead of being
    /// normalised to something else, and that the failure names the volume rather than anything else.
    /// </para>
    /// </remarks>
    private static async Task RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var file = Path.Combine(ScratchWorkspace.Root, $"refused-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var options = new RecycleOptions { ThrowOnError = false };

        var result = await RecycleBin
            .RecycleAsync([file], options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.False(result.Succeeded, $"a volume with no Recycle Bin cannot report success (recycled {result.RecycledPaths.Count})");
        Check.Equal(1, result.Failures.Count, "the refused path is the only failure reported");
        Check.Equal(RecycleFailureStage.NoRecycleBinOnVolume, result.FirstFailure!.Stage, "the failure names the volume");
        Check.Equal(file, result.FirstFailure.Path, "the failure names the caller's path");
        Check.Equal(0, result.FirstFailure.Attempts, "nothing was attempted, because nothing was handed over");
        Check.Equal(0, result.FirstFailure.HResult, "the refusal is settled from configuration, so it carries no code");

        Check.Equal(0, result.PermanentlyDeletedPaths.Count, "nothing was removed permanently either");
        Check.Equal(0, result.RecycledPaths.Count, "a refused path is not reported as recycled");
        Check.True(File.Exists(file), "the file is still on disk, which is the whole point");

        workspace.Dispose();
    }

    /// <summary>
    /// The same refusal raises under <see cref="RecycleBinOptions.ThrowOnError"/>, and the file is still
    /// there when the exception surfaces.
    /// </summary>
    /// <remarks>
    /// <see cref="IOException"/> rather than a new exception type: the single-path overload already
    /// documents a refusal as an <see cref="IOException"/>. The file assertion is inside the same call's
    /// aftermath, so a caller that catches the exception and then looks at the path learns the truth.
    /// <para>
    /// The file lives on the volume <c>--scratch</c> named and the case needs that volume to have no
    /// Recycle Bin, so it runs where <see cref="ScratchWorkspace.RequireNonRecycling"/> allows and is a
    /// skip rather than a failure elsewhere.
    /// </para>
    /// </remarks>
    private static async Task RefusedPathRaisesWhenThrowOnErrorIsOn(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var file = Path.Combine(ScratchWorkspace.Root, $"refused-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var thrown = await Check
            .ThrowsAsync<IOException>(
                () => RecycleBin.RecycleAsync(file, RecycleOptions.Default, cancellationToken: context.CancellationToken),
                "a refused volume raises on the single-path overload when ThrowOnError is on")
            .ConfigureAwait(false);

        Check.Equal(0, thrown.HResult, "the refusal is settled from configuration, so it carries no code");

        Check.True(File.Exists(file), "the file survived the exception, because nothing was attempted");

        workspace.Dispose();
    }

    /// <summary>
    /// The single-path overload reports <see langword="false"/> for a refused path, which is what a
    /// caller branching on the return value will act on.
    /// </summary>
    /// <remarks>
    /// A regression here is the quiet kind: returning <see langword="true"/> would tell the caller the
    /// file is gone when it is on disk, and the caller would not look any further.
    /// <para>
    /// The refusal it needs comes from a volume with no Recycle Bin, so the file is written on the volume
    /// <c>--scratch</c> named and the case is a skip on a volume that recycles.
    /// </para>
    /// </remarks>
    private static async Task SinglePathOverloadReturnsFalseWhenRefused(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var file = Path.Combine(ScratchWorkspace.Root, $"refused-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var options = new RecycleOptions { ThrowOnError = false };

        var removed = await RecycleBin.RecycleAsync(file, options, context.CancellationToken).ConfigureAwait(false);

        Check.False(removed, "a refused path reports false rather than throwing");
        Check.True(File.Exists(file), "and the file is still there, so false is the honest answer");

        workspace.Dispose();
    }

    /// <summary>
    /// A path that is not there is refused at <see cref="RecycleFailureStage.PathValidation"/> without a
    /// probe, because there is no volume question to ask about a file that does not exist.
    /// </summary>
    /// <remarks>
    /// This is the case that keeps the gate cheap: a batch of missing paths pays one filesystem probe
    /// each and nothing else. It also pins that the refusal is decided on existence rather than on the
    /// volume — a missing path on a healthy volume and a missing path on a bin-less one are the same
    /// failure — so it runs on whatever volume <c>--scratch</c> named rather than on a volume
    /// manufactured to be certain. A missing path in the run's own directory is the honest form of that
    /// claim: the path is absent for the ordinary reason, and the volume is never asked about at all.
    /// </remarks>
    private static async Task AMissingPathIsRefusedWithoutAProbe(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var missing = Path.Combine(ScratchWorkspace.Root, $"definitely-absent-{Environment.ProcessId}.bin");
        var options = new RecycleOptions { ThrowOnError = false };

        var result = await RecycleBin
            .RecycleAsync([missing], options, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.False(result.Succeeded, "a path that is not there cannot succeed");
        Check.Equal(1, result.Failures.Count, "it is reported once");
        Check.Equal(missing, result.FirstFailure!.Path, "naming the path the caller passed");
        Check.Equal(RecycleFailureStage.PathValidation, result.FirstFailure.Stage, "and it fails at validation, not at the volume that holds it");
        Check.Equal(0, result.FirstFailure.Attempts, "nothing was handed over, because there was nothing to hand over");
        Check.Equal(0, result.FirstFailure.HResult, "no code is involved when nothing was ever asked");

        workspace.Dispose();
    }

    /// <summary>
    /// <see cref="NoRecycleBinAction.AllowIndeterminate"/> is what lets a caller recycle a path on
    /// a volume whose answer was neither <see cref="RecycleBinAvailabilityReason.Enabled"/> nor one of the
    /// confirmed refusals.
    /// </summary>
    /// <remarks>
    /// This is the only case here that watches a file leave the disk, and deliberately so: the option
    /// says the caller accepts being wrong about the bin, so the path is recycled anyway and the outcome
    /// is reported as recycled. What the case proves is that the option reaches the pipeline at all — a
    /// gate that refused regardless of the setting would fail it — and that a passing path is reported as
    /// recycled.
    /// <para>
    /// The option only changes the outcome on a volume with no Recycle Bin, so the file is written on the
    /// volume <c>--scratch</c> named and the case is a skip on a volume that recycles, where the option
    /// would be a no-op and would prove nothing.
    /// </para>
    /// <para>
    /// The gate is written out rather than delegated to <see cref="ScratchWorkspace.RequireNonRecycling"/>,
    /// because that guard admits one reason too many for this claim.
    /// <see cref="RecycleBinVolumePolicy.RefusesRecycle"/> refuses
    /// <see cref="RecycleBinAvailabilityReason.UnsupportedVolumeType"/>,
    /// <see cref="RecycleBinAvailabilityReason.DisabledByPolicy"/> and
    /// <see cref="RecycleBinAvailabilityReason.DisabledByUserPreference"/> under every action but
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> — the option buys no reach there, because those
    /// three are the library's own decision that there is no bin to hand a file to, not a doubt about one.
    /// <see cref="RecycleBinAvailabilityReason.UnsupportedVolumeType"/> is what a share reads as, and a
    /// share is a <c>--scratch</c> root both documents accept, so under the coarser guard this case would
    /// go red on exactly the volumes the harness promises it can be run against, over an assertion about a
    /// handover that policy had already refused.
    /// <see cref="ContractSuite.EveryReasonAndActionPairIsPinned"/> already pins that refusal on every
    /// machine without one, which is what makes narrowing the honest choice rather than a quiet loss: what
    /// would have been asserted here is asserted there, and what remains here is the claim in this case's
    /// name.
    /// </para>
    /// <para>
    /// The guard itself is unchanged: nine other cases mean "not Enabled" by
    /// <see cref="ScratchWorkspace.RequireNonRecycling"/> and assert a refused path, which is the correct
    /// reading on all three confirmed reasons; narrowing it would have taken their coverage with it.
    /// </para>
    /// </remarks>
    private static async Task NoRecycleBinOptionDecidesWhetherThePathIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        if (ScratchWorkspace.Reason is not (RecycleBinAvailabilityReason.NoRecycleBinOnVolume
            or RecycleBinAvailabilityReason.Indeterminate))
        {
            Check.Skip(
                $"{ScratchWorkspace.VolumeRoot} reads as {ScratchWorkspace.Reason}. "
                + "This case needs a volume whose answer is a doubt rather than a refusal");
        }

        var file = Path.Combine(ScratchWorkspace.Root, $"allowed-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        // This case is not only an assertion: it is the one thing in the suite that creates a Recycle Bin
        // on a volume that had none, because AllowIndeterminate recycles the path anyway and the answer is
        // to make somewhere for it rather than destroy it. That is the documented behaviour and the case is
        // what pins it — but a case that leaves the operator's volume configured differently than it found
        // it is a bad neighbour, so what it caused is put back.
        var binDirectory = Path.Combine(ScratchWorkspace.VolumeRoot, "$RECYCLE.BIN", NativeBinLayout.CurrentSid);
        var binExisted = Directory.Exists(binDirectory);

        var options = new RecycleOptions
        {
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.AllowIndeterminate,
        };

        try
        {
            var result = await RecycleBin
                .RecycleAsync([file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.True(
                result.Succeeded,
                $"AllowIndeterminate recycles the path anyway (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
            Check.Equal(1, result.RecycledPaths.Count, "the path comes back as recycled");
            Check.Equal(file, result.RecycledPaths[0], "in the spelling the caller supplied");
            Check.Equal(0, result.Failures.Count, "the volume's answer did not become a failure");
            Check.False(File.Exists(file), "the file is gone from disk, which is the risk the caller accepted");

            if (!binExisted)
            {
                Check.True(Directory.Exists(binDirectory),
                    $"somewhere was made for the file rather than destroying it, so it created {binDirectory}");
            }
        }
        finally
        {
            RemoveBinIfThisCaseMadeIt(binDirectory, binExisted);
            workspace.Dispose();
        }
    }

    /// <summary>
    /// Removes a Recycle Bin directory this case created on the run's volume, and only that.
    /// </summary>
    /// <remarks>
    /// Two conditions, both required. The directory must not have existed when the case started, or the
    /// caller had one and removing it would destroy their Recycle Bin. Everything inside it is then this
    /// case's own work — the volume had no bin a moment ago — so it is emptied through the library's own
    /// path rather than by hand, and the directory removed afterwards.
    /// </remarks>
    private static async Task RemoveBinIfThisCaseMadeIt(string binDirectory, bool existedBefore)
    {
        if (existedBefore || !Directory.Exists(binDirectory))
        {
            return;
        }

        try
        {
            await RecycleBin
                .EmptyAsync(ScratchWorkspace.VolumeRoot, new RecycleOptions { ThrowOnError = false })
                .ConfigureAwait(false);

            Directory.Delete(binDirectory);

            var parent = Path.GetDirectoryName(binDirectory);

            // Only when nothing else is under $RECYCLE.BIN. Another account's directory on a multi-user
            // machine is not this case's to remove, and the folder itself belongs to Windows.
            if (parent is not null
                && string.Equals(Path.GetFileName(parent), "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The case's assertions already ran. A volume that will not give the directory back is the
            // operator's to deal with, not a reason to fail a case about a different thing.
        }
    }

    /// <summary>
    /// One volume in a batch that cannot recycle a path of the caller's is not the whole batch's verdict:
    /// the paths on the volumes that are there are still recycled.
    /// </summary>
    /// <remarks>
    /// The gate groups a batch by volume root, so this is the case that fails if it inspects only the
    /// first root, and the case that fails if it refuses the batch as a whole. Both are silent failures
    /// of the other kind — a batch that skips the healthy paths, and a batch that gives up on all of them
    /// because of one.
    /// <para>
    /// The second volume is a drive letter nothing is mounted on, found on this machine rather than named
    /// in the source. Its path fails at <see cref="RecycleFailureStage.PathValidation"/> rather than at
    /// <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/>, and that is the whole story about this
    /// shape of bad volume: the path is not there either, and a path that is not there is settled before
    /// any volume is asked about. Asserting the volume stage here would claim a detail this case never
    /// measures — no volume with a bin in it is involved on either side of the batch. What is measured is
    /// that one unusable path leaves the rest of the batch alone, and the healthy path is on the volume
    /// that really is there and really does recycle.
    /// </para>
    /// <para>
    /// Which means the case needs a volume that recycles, and says so rather than running on one that does
    /// not: there, both halves are refused by the same gate and the contrast this case is about never
    /// appears.
    /// </para>
    /// </remarks>
    private static async Task OneBadVolumeDoesNotSinkTheBatch(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        // The healthy half of this batch is a file on the run's own volume, so it only recycles where that
        // volume recycles. On a volume that does not, both halves are refused by the same gate and there is
        // nothing left to contrast — which is the whole claim here.
        ScratchWorkspace.RequireRecycling("the batch needs one path that recycles and one that does not");

        var letter = AbsentVolume.Letter();
        var healthy = workspace.CreateRandomFiles(1, 4096)[0];
        var refused = Path.Combine(AbsentVolume.RootOf(letter), "on-a-volume-that-is-not-there.bin");

        var options = new RecycleOptions { ThrowOnError = false };
        var batch = new List<string?> { refused, healthy };

        var result = await RecycleBin
            .RecycleAsync(batch, options, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);

        Check.False(result.Succeeded, "the refused path still makes the batch report failure");
        Check.Equal(1, result.Failures.Count, "exactly one failure, from the volume that is not there");
        Check.Equal(refused, result.FirstFailure!.Path, "and it is the path the caller supplied for it");
        Check.Equal(RecycleFailureStage.PathValidation, result.FirstFailure.Stage, "a path on a volume that is not there is refused before the volume is asked about");
        Check.Equal(1, result.RecycledPaths.Count, "the path on the volume that is there was still recycled");
        Check.Equal(healthy, result.RecycledPaths[0], "and it is the one the caller supplied");
        Check.False(File.Exists(healthy), "so that file is gone from disk");

        workspace.Dispose();
    }

    /// <summary>
    /// A path listed twice on a volume that cannot recycle is refused once.
    /// </summary>
    /// <remarks>
    /// The gate answers before <see cref="RecycleTarget.CollapseEquivalent"/> gets a chance
    /// to, so without collapsing here a caller who listed one path twice would read two failures for one
    /// file. Both spellings stay in the batch on purpose: what is being checked is the gate's
    /// bookkeeping, not the collapse.
    /// <para>
    /// The gate the bookkeeping belongs to needs a volume with no Recycle Bin, so the file is written on
    /// the volume <c>--scratch</c> named and the case is a skip on a volume that recycles.
    /// </para>
    /// </remarks>
    private static async Task ARepeatedRefusedPathIsRefusedOnce(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var file = Path.Combine(ScratchWorkspace.Root, $"refused-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var options = new RecycleOptions { ThrowOnError = false };

        var result = await RecycleBin
            .RecycleAsync([file, file], options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.False(result.Succeeded, "the refused path still fails the operation");
        Check.Equal(1, result.Failures.Count, "one path is refused once, however many times it was listed");
        Check.Equal(file, result.FirstFailure!.Path, "and the failure names the caller's spelling");
        Check.Equal(0, result.RecycledPaths.Count, "nothing was attempted, so nothing was recycled");
        Check.True(File.Exists(file), "the file is untouched");

        workspace.Dispose();
    }

    /// <summary>
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> is the only answer a caller can give that loses
    /// the file by choice, so it has to arrive as
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> rather than as a success nobody can explain.
    /// </summary>
    /// <remarks>
    /// The refusal above is what a caller who never asked for this gets on the very same volume. This is
    /// its opposite: the file goes, and the result says so in the one list that says so.
    /// <see cref="RecycleBinResult.RecycledPaths"/> is asserted empty rather than merely smaller, because
    /// that is the claim a caller reads two lists for: a path in both would make the lists contradict each
    /// other, which is worse than either one being wrong on its own.
    /// <para>
    /// The option is only a choice on a volume that cannot recycle, so the file is written on the volume
    /// <c>--scratch</c> named and the case is a skip on a volume that recycles, where the file would go
    /// to the Recycle Bin and the option would be a no-op.
    /// </para>
    /// </remarks>
    private static async Task DeletePermanentlyRemovesTheFileAndNamesIt(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var file = Path.Combine(ScratchWorkspace.Root, $"signed-away-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var options = new RecycleOptions
        {
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently,
        };

        var result = await RecycleBin
            .RecycleAsync([file], options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(result.Succeeded, $"the caller asked for the file to go (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(1, result.PermanentlyDeletedPaths.Count, "the removed path is named as removed");
        Check.Equal(file, result.PermanentlyDeletedPaths[0], "in the spelling the caller supplied");
        Check.Equal(0, result.RecycledPaths.Count, "and it is not also reported as recycled");
        Check.Equal(0, result.Failures.Count, "removing a file the caller signed away is not a failure");
        Check.False(File.Exists(file), "the file is gone from disk, which is the whole point of the option");

        workspace.Dispose();
    }

    /// <summary>
    /// A directory is removed by the option as readily as a file, and by a call that understands it is one.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is quiet in the code and loud in the field: <c>File.Delete</c> on a
    /// directory raises <see cref="UnauthorizedAccessException"/>, which reads as a permission problem and
    /// sends an integrator looking for an access bug that is not there. What is asserted here is the
    /// outcome — the tree is gone, the directory is named, and nothing escaped as an exception, since
    /// <c>ThrowOnError</c> is off and a refusal would have been a reported failure rather than a throw.
    /// <para>
    /// The option reaches this call only on a volume that cannot recycle, so the tree is built on the
    /// volume <c>--scratch</c> named and the case is a skip on a volume that recycles.
    /// </para>
    /// </remarks>
    private static async Task DeletePermanentlyRemovesADirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var directory = Path.Combine(ScratchWorkspace.Root, $"tree-{Environment.ProcessId}");
        var inner = Path.Combine(directory, "nested");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "a.txt"), "hello");

        var options = new RecycleOptions
        {
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently,
        };

        var result = await RecycleBin
            .RecycleAsync([directory], options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(
            result.Succeeded,
            $"a directory the caller signed away is removed rather than refused (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(1, result.PermanentlyDeletedPaths.Count, "the directory is named as removed");
        Check.Equal(directory, result.PermanentlyDeletedPaths[0], "in the spelling the caller supplied");
        Check.False(Directory.Exists(directory), "the whole tree is gone from disk, contents included");
        Check.Equal(0, result.RecycledPaths.Count, "and it was not recycled, on a volume that cannot recycle");

        workspace.Dispose();
    }

    /// <summary>
    /// A file system that refuses the removal is reported as such, with the file system's own code, and
    /// the file stays exactly where the caller left it.
    /// </summary>
    /// <remarks>
    /// The hold is what a directory another process is working in looks like, and it is the only way to make
    /// the file system say no on demand. The stage is pinned rather than the exception type because the
    /// contract is the report, not the throw: with <see cref="RecycleBinOptions.ThrowOnError"/> off there is
    /// none. A stage of <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/> here would say the caller had
    /// not consented — the opposite of what was asked, and the reason the stage is checked rather than left.
    /// <para>
    /// A removal is only this library's own on a volume that cannot recycle, so the file is written on the
    /// volume <c>--scratch</c> named and the case is a skip on a volume that recycles.
    /// </para>
    /// </remarks>
    private static async Task DeletePermanentlyReportsAFileSystemRefusal(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var file = Path.Combine(ScratchWorkspace.Root, $"held-{Environment.ProcessId}.bin");
        File.WriteAllBytes(file, new byte[4096]);

        var options = new RecycleOptions
        {
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently,
        };

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await RecycleBin
                .RecycleAsync([file], options, context.CancellationToken)
                .ConfigureAwait(false);

            Check.False(result.Succeeded, "a removal the file system refused is a failure");
            Check.Equal(1, result.Failures.Count, "and it is the only one reported");
            Check.Equal(file, result.FirstFailure!.Path, "naming the caller's path");
            Check.Equal(
                RecycleFailureStage.PermanentDeleteFailed,
                result.FirstFailure.Stage,
                "at the removal itself, not at the volume that held it");
            Check.Equal(
                0,
                result.FirstFailure.Attempts,
                "nothing was recorded as attempted, because the removal is this library's own");
            Check.NotEqual(0, result.FirstFailure.HResult, "the file system's own error code came through");
            Check.True(
                (result.FirstFailure.HResult & unchecked((int)0x80000000)) != 0,
                $"that code is a refusal rather than a success (got 0x{result.FirstFailure.HResult:X8})");
            Check.Equal(0, result.PermanentlyDeletedPaths.Count, "and nothing was claimed as removed");
            Check.Equal(0, result.RecycledPaths.Count, "nor as recycled");
        }

        Check.True(File.Exists(file), "the file is still there, because the refusal was the file system's and not the library's");

        workspace.Dispose();
    }

    /// <summary>
    /// A path is in at most one of the two lists, whatever mix of volumes and outcomes produced the
    /// result — the property a caller reads both lists for.
    /// </summary>
    /// <remarks>
    /// The invariant rather than either outcome: a path cannot be both, so a caller cannot be told the file
    /// went to the Recycle Bin and was also destroyed.
    /// <para>
    /// A batch of two rather than one path, because a single path makes the two lists indistinguishable
    /// from one — a bug that reported a permanently deleted path as recycled would also report the reverse
    /// as true. Two paths on the run's own volume, both of which the option removes, is what the batch can
    /// honestly be here: a call carries one <see cref="RecycleBinOptions.NoRecycleBinAction"/> and the
    /// volume it runs on answers for every path in it, so a batch that reached one branch and another has
    /// to span two volumes of different kinds, and this run names one volume. What the assertions still
    /// catch is the recording rather than the mixture: a path in both lists, and a path recorded in one
    /// list twice.
    /// </para>
    /// <para>
    /// Compared without regard to case, because the two lists are written by different code and a caller
    /// comparing them should not have to know that.
    /// </para>
    /// </remarks>
    private static async Task APathIsNeverBothRecycledAndPermanentlyDeleted(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var files = workspace.CreateRandomFiles(2, 4096);

        var options = new RecycleOptions
        {
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently,
        };

        var result = await RecycleBin
            .RecycleAsync(files, options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(result.Succeeded, $"both paths were handled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(2, result.PermanentlyDeletedPaths.Count, "both paths were removed for good, as the caller asked");
        Check.Equal(0, result.RecycledPaths.Count, "and neither was reported as recycled, so neither is in both lists");

        var both = result.RecycledPaths
            .Intersect(result.PermanentlyDeletedPaths, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Check.Equal(0, both.Length, $"no path is in both lists, because the two cannot both be true of one file (overlap: {string.Join(", ", both)})");
        Check.Equal(
            result.RecycledPaths.Count + result.PermanentlyDeletedPaths.Count,
            files.Count,
            "and each of the two paths is in one list, so no path was recorded in both of them");

        workspace.Dispose();
    }

    /// <summary>
    /// The extended-length prefix changes which path the file APIs take, not where the file is. A short
    /// path handed over in that form is still the same volume, so the delete has to be verified and
    /// reported as an ordinary recycle.
    /// </summary>
    /// <remarks>
    /// The caller's spelling keeps the <c>\\?\</c> prefix while the normalised paths the pipeline
    /// compares do not. The volume comparison settles that first, so every prefixed path reads as the
    /// volume it names rather than as a volume that is not there.
    /// </remarks>
    private static async Task ExtendedPrefixOnAShortPathStillReportsSuccess(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for a prefixed short path's bytes to be counted in");

        var plain = workspace.CreateRandomFile(4096);
        var extended = ExtendedPath.For(plain);
        var volumeRoot = Path.GetPathRoot(plain)!;

        Check.True(plain.Length <= 260, $"the path is short enough to recycle unprefixed ({plain.Length} chars)");

        var (_, sizeBefore) = RecycleBinProbe.MeasureOrFail(volumeRoot, "the baseline size");

        var result = await RecycleBin.RecycleAsync([extended], RecycleOptions.Default, cancellationToken: context.CancellationToken);

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
            () => RecycleBin.RecycleAsync(missing).GetAwaiter().GetResult(),
            "a missing path on the single-path overload reports the path, not a move failure");

        Check.True(
            thrown.Message.Contains(missing, StringComparison.OrdinalIgnoreCase),
            $"the exception names the missing path (got '{thrown.Message}')");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static async Task SingleLockedPathThrowsWithFileSystemHResult(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        // The claim is about the code the file system itself gave. A volume whose recycling is settled from
        // configuration is refused before it is reached, and such a refusal carries HResult 0 because there
        // is no file-system code to report, which is what RecycleFailure.HResult documents. Guarded ahead of
        // the file so a skip leaves nothing behind.
        ScratchWorkspace.RequireRecycling("a file-system refusal to carry the file system's own error code");

        var files = workspace.CreateRandomFiles(1, 4096);

        IOException thrown;
        using (new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var options = new RecycleOptions
            {
                MaxAttempts = 1,
                InitialRetryDelay = TimeSpan.Zero,
                OverallTimeout = TimeSpan.FromSeconds(5)
            };

            var batch = await RecycleBin.RecycleAsync([files[0]], options, context.CancellationToken);
            Check.False(batch.Succeeded, "a locked file cannot be recycled");

            thrown = Check.Throws<IOException>(
                () => RecycleBin.RecycleAsync(files[0], cancellationToken: context.CancellationToken).GetAwaiter().GetResult(),
                "a file-system refusal on the single-path overload raises IOException");
        }

        Check.True((thrown.HResult & unchecked((int)0x80000000)) != 0, $"the exception carries the file-system error code (got 0x{thrown.HResult:X8})");
        Check.True(thrown.InnerException is not null, "the code is also available as an inner exception");
        Check.True(File.Exists(files[0]), "the file is still there, since the move was refused");

        workspace.Dispose();
    }

    private static async Task SingleFileIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the one file to land in and be counted in");
        var files = workspace.CreateRandomFiles(1, 4096);
        var (before, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");

        var recycled = await RecycleBin.RecycleAsync(files[0], cancellationToken: context.CancellationToken);

        Check.True(recycled, "a single recycled file reports success");
        Check.False(File.Exists(files[0]), "the file is gone from disk");
        Check.True(RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - before >= 1, "the Recycle Bin gained the item");

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
        ScratchWorkspace.RequireRecycling("a batch of generated files to arrive in a Recycle Bin");
        var files = workspace.CreateFixtureSet();
        var (before, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");

        var result = await RecycleBin.RecycleAsync(files, RecycleOptions.Default, cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"every generated file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8} {result.FirstFailure?.Path})");
        Check.Equal(files.Count, result.RecycledPaths.Count, "every generated file is reported as recycled");
        Check.Equal(0, result.Failures.Count, "no generated file is reported as failed");

        foreach (var file in files)
        {
            Check.False(File.Exists(file), $"{Path.GetFileName(file)} is gone from disk");
        }

        Check.True(RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - before >= files.Count, "the Recycle Bin gained every generated file");

        workspace.Dispose();
    }

    private static Task NullPathThrowsWhenThrowOnError(TestContext context)
    {
        Check.Throws<ArgumentException>(
            () => RecycleBin.RecycleAsync((string?)null).GetAwaiter().GetResult(),
            "a null path with throwOnError is rejected eagerly");

        Check.Throws<ArgumentException>(
            () => RecycleBin.RecycleAsync("   ").GetAwaiter().GetResult(),
            "a whitespace path with throwOnError is rejected eagerly");

        return Task.CompletedTask;
    }

    private static async Task NullPathFailsWithoutThrowOnError(TestContext context)
    {
        // Throwing off, stated rather than inherited: the single-path overload decides between raising and
        // returning false on exactly this flag, and a case named for it that relied on the default was
        // asserting the opposite of what it said.
        var options = new RecycleOptions { ThrowOnError = false };

        Check.False(
            await RecycleBin.RecycleAsync((string?)null, options, context.CancellationToken).ConfigureAwait(false),
            "the single-path overload answers false for a null path when throwing is off, rather than raising");

        Check.False(
            await RecycleBin.RecycleAsync("   ", options, context.CancellationToken).ConfigureAwait(false),
            "and the same for a whitespace path, which names nothing either");

        var result = await RecycleBin
            .RecycleAsync(new string?[] { null }, options, context.CancellationToken)
            .ConfigureAwait(false);

        Check.False(result.Succeeded, "a null path without throwOnError reports failure instead of throwing");
        Check.Equal(1, result.Failures.Count, "exactly one failure is reported");
        Check.Equal(RecycleFailureStage.PathValidation, result.Failures[0].Stage, "the failure is a path validation failure");
        Check.Equal(0, result.Failures[0].HResult, "no code is involved in a validation failure");
        Check.Equal(0, result.RecycledPaths.Count, "nothing was recycled");
    }

    private static async Task MissingPathDoesNotBlockTheRest(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the five real files of the batch to reach");
        var files = workspace.CreateRandomFiles(5, 4096);
        var missing = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

        var batch = new List<string?> { missing };
        batch.AddRange(files);

        var result = await RecycleBin.RecycleAsync(batch, RecycleOptions.Default, cancellationToken: context.CancellationToken);

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
        var result = await RecycleBin.RecycleAsync(Array.Empty<string?>(), cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, "an empty batch is a success");
        Check.Equal(0, result.Failures.Count, "an empty batch reports no failures");
        Check.Equal(0, result.RecycledPaths.Count, "an empty batch recycles nothing");
    }

    private static async Task DirectoryTargetIsRecycledWithContents(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for a directory to land in as one entry");

        var directory = Path.Combine(ScratchWorkspace.Root, "tree", "nested");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(ScratchWorkspace.Root, "tree", "b.txt"), "world");

        var (before, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");
        var result = await RecycleBin.RecycleAsync([directory], RecycleOptions.Default, cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a directory is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(Directory.Exists(directory), "the directory tree is gone from disk");
        Check.True(RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - before >= 1, "the Recycle Bin gained the directory");

        workspace.Dispose();
    }

    /// <summary>
    /// A path hundreds of characters long recycles under default options: no staging, no policy, no
    /// limit. The destination inside the bin is short whatever the source was.
    /// </summary>
    private static async Task LongPathRecyclesWithNoStagingNoPolicyNoLimit(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for a long path to land in");

        var deep = Path.Combine(ScratchWorkspace.Root, new string('d', 120));
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, new string('f', 130) + ".bin");

        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(new byte[2048]);
        }

        Check.True(file.Length > 260, $"the test path is genuinely long ({file.Length} chars)");

        var result = await RecycleBin.RecycleAsync([file], RecycleOptions.Default, cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a long path is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.Equal(1, result.RecycledPaths.Count, "and is named as recycled");
        Check.Equal(0, result.PermanentlyDeletedPaths.Count, "and never removed for good on the way");
        Check.False(File.Exists(file), "so the file is in the Recycle Bin rather than left where it was");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken);
        Check.True(
            Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).Any(item => string.Equals(item.OriginalPath, file, StringComparison.OrdinalIgnoreCase)),
            "the entry carries the whole original path, however long it was");

        workspace.Dispose();
    }

    private static async Task LargeFileIsRecycled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for 10 MB to be measured arriving in");

        const int size = 10 * 1024 * 1024;
        var files = workspace.CreateRandomFiles(1, size);
        var (_, before) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline size");

        var result = await RecycleBin.RecycleAsync([files[0]], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"a 10 MB file is recycled (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(File.Exists(files[0]), "the 10 MB file is gone from disk");
        Check.True(RecycleBinProbe.QuerySize(ScratchWorkspace.VolumeRoot) - before >= size, "the Recycle Bin grew by the full 10 MB, so the bytes were really moved");

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
            () => RecycleBin.RecycleAsync(files, cancellationToken: cancelled.Token).GetAwaiter().GetResult(),
            "an already-cancelled token is observed instead of recycling");

        foreach (var file in files)
        {
            Check.True(File.Exists(file), $"{Path.GetFileName(file)} was left alone, as the call never started");
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A batch is validated before any of it is recycled, one filesystem probe per path, and that scan is
    /// the long half of a large call — about 11 ms a path on the volume measured here, which is minutes for
    /// twenty thousand paths. Nothing in it looks at the caller's token, so a caller who cancels while it
    /// runs is made to wait for the whole scan before being told, and the wait is on a pool thread with
    /// nothing to show for it.
    /// </summary>
    /// <remarks>
    /// Driven through the private method rather than through the public one, because the public one can show
    /// this only by losing a race against a timer, and a batch large enough to lose it reliably would be a
    /// load case in its own right — which would put the claim behind a flag over a check that costs nothing.
    /// Nothing here touches the disk either: the paths need not exist, because the token has to be read
    /// before the first of them is looked at, and a path that does not exist is a validation failure rather
    /// than a fault.
    /// </remarks>
    private static Task CancellationIsObservedWhileABatchIsValidated(TestContext context)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var paths = new List<string?> { Path.Combine(ScratchWorkspace.Root, "never-created-1.bin") };

        Check.Throws<OperationCanceledException>(
            () => BuildTargets(paths, RecycleOptions.Default, cancelled.Token),
            "a token already cancelled stops the validation instead of every path being probed first");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Calls the private batch validator the way <see cref="RecycleBatch.Run"/> does through
    /// <see cref="RecycleBin.RecycleAsync(IReadOnlyList{string?}, RecycleBinOptions?, CancellationToken)"/>,
    /// unwrapping what reflection hands back.
    /// </summary>
    private static void BuildTargets(
        IReadOnlyList<string?> filePaths,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        var build = typeof(RecycleBatch).GetMethod("BuildTargets", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(RecycleBatch).FullName, nameof(BuildTargets));

        try
        {
            build.Invoke(null, [filePaths, options, cancellationToken]);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            throw wrapped.InnerException;
        }
    }
}
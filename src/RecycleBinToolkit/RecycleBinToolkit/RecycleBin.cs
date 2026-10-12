using System.Diagnostics;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves files and directories into the Windows Recycle Bin, and reads, empties and reports on the bin
/// itself.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
public static class RecycleBin
{
    /// <summary>Win32 <c>ERROR_PATH_NOT_FOUND</c>, for a path that is not a volume root.</summary>
    private const int VolumeNotFound = unchecked((int)0x800703FE);

    /// <summary>Recycles a single file or directory.</summary>
    /// <param name="filePath">Path of the file or directory to recycle.</param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RecycleOptions.Default"/>.
    /// <see cref="RecycleOptions.ThrowOnError"/> decides whether a failure raises or comes back as
    /// <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the validation scan and the work between paths.</param>
    /// <returns><see langword="true"/> when the path was removed from disk.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is null, empty or whitespace and
    /// <see cref="RecycleOptions.ThrowOnError"/> is on.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist and <see cref="RecycleOptions.ThrowOnError"/> is
    /// on.
    /// </exception>
    /// <exception cref="IOException">
    /// The move failed, or the volume holding the path cannot recycle it and
    /// <see cref="RecycleOptions.NoRecycleBinAction"/> is <see cref="NoRecycleBinAction.Throw"/>, or
    /// that action is <see cref="NoRecycleBinAction.DeletePermanently"/> and the file system refused
    /// the removal — and <see cref="RecycleOptions.ThrowOnError"/> is on. The file is left in place
    /// in every one of those cases.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The path names its volume by GUID, which this library cannot measure, and
    /// <see cref="RecycleOptions.ThrowOnError"/> is on. Nothing was attempted; address the same
    /// location by drive letter or mount point instead.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<bool> RecycleAsync(
        string? filePath,
        RecycleOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? RecycleOptions.Default;

        if (effective.ThrowOnError)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        }
        else if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var result = await RecycleAsync([filePath], effective, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return true;
        }

        if (effective.ThrowOnError)
        {
            ThrowForSinglePath(result);
        }

        return false;
    }

    /// <summary>Recycles a batch of files or directories.</summary>
    /// <param name="filePaths">
    /// Paths to recycle. Entries may be <see langword="null"/> or name paths that do not exist; each such
    /// entry is reported as a failure without aborting the rest of the batch. Repeated paths are
    /// recycled once.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RecycleOptions.Default"/>.
    /// <see cref="RecycleOptions.ThrowOnError"/> is a single-path switch and the batch reports every
    /// per-item failure through <see cref="RecycleBinResult.Failures"/>, so setting it changes nothing
    /// on this overload.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the validation scan and the work between paths. A cancelled batch raises and returns no
    /// result: what went is found by reading the bin.
    /// </param>
    /// <returns>
    /// The outcome of the batch, naming every path that was not handled in
    /// <see cref="RecycleBinResult.Failures"/>. Never throws for a per-item failure. A path refused
    /// because its volume cannot recycle it arrives there at stage
    /// <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/>, and the paths on every other volume in
    /// the batch are still recycled.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, whether before the work started or while the
    /// batch was being validated.
    /// </exception>
    public static async Task<RecycleBinResult> RecycleAsync(
        IReadOnlyList<string?> filePaths,
        RecycleOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<RecycleBinResult>(cancellationToken).ConfigureAwait(false);
        }

        var effective = options ?? RecycleOptions.Default;

        // Before argument validation, before any filesystem question, and before the hop to the pool: the
        // machine either can be written to or cannot, and that does not depend on the paths, so a
        // caller is told before this library starts touching anything. It is not a RecycleFailure and
        // ThrowOnError does not suppress it — nothing was attempted, so there is nothing to report as data.
        RecycleBinBackendGuard.RequireWriteSupport();

        // Validating a batch is one filesystem probe per path and, on the volume measured here, about
        // 11 ms each — so 20,000 paths is a little over three and a half minutes. Run inline that time is
        // the caller's own, because an async method runs everything before its first await on the
        // thread that called it: a caller on a UI thread would be frozen for the whole scan, and a caller
        // holding a lock would hold it. Hopping to the pool first costs a scheduling round trip and buys
        // back the caller's thread, which is the only one that has something else to be doing.
        return await Task
            .Run(() => RecycleBatch.Run(filePaths, effective, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Raises what the single-path caller is told, from the outcome the batch came back with.
    /// </summary>
    private static void ThrowForSinglePath(RecycleBinResult result)
    {
        var failure = result.FirstFailure;
        if (failure is null)
        {
            return;
        }

        switch (failure.Stage)
        {
            case RecycleFailureStage.VolumeGuidPathNotAddressable:
                RecycleBinThrowHelper.ThrowVolumeNotAddressableByGuid(failure);
                break;

            case RecycleFailureStage.PathValidation when !string.IsNullOrEmpty(failure.Path):
                RecycleBinThrowHelper.ThrowFileNotFound(failure.Path);
                break;

            default:
                RecycleBinThrowHelper.ThrowRecycleFailed(failure);
                break;
        }
    }

    /// <summary>Reads the contents of the Recycle Bin.</summary>
    /// <param name="volumeRoot">
    /// Volume root to keep, such as <c>"X:\\"</c>, or <see langword="null"/> for every volume. A path inside
    /// a volume is not a volume root: it is refused with a <see cref="RecycleFailureStage.PathValidation"/>
    /// failure rather than answered for the whole volume containing it, because an empty listing is a claim
    /// about the bin and a caller whose root was one character wrong cannot tell that from a bin that really
    /// is empty.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="GetItemsOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The entries that could be read, grouped by volume in the order the volumes were asked about, plus the
    /// ones that could not in <see cref="RecycleBinContents.Failures"/>. An environmental problem is never
    /// an exception.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<RecycleBinContents> GetItemsAsync(
        string? volumeRoot = null,
        GetItemsOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? GetItemsOptions.Default;
        var stopwatch = Stopwatch.StartNew();

        // A path that is not a volume root names no volume of its own. Widening it to the one containing it
        // would hand a caller who mistyped a root the whole volume's bin, with no way to tell that from what
        // they asked for. Refused rather than widened, which is what EmptyAsync does with the same input and
        // what GetStatusAsync answers it with.
        var targets = RecycleBinVolumeSet.Resolve(volumeRoot);
        if (targets is null)
        {
            return new RecycleBinContents(
                [],
                [new RecycleFailure(volumeRoot!, VolumeNotFound, 0, RecycleFailureStage.PathValidation)],
                stopwatch.Elapsed);
        }

        var collected = await Task
            .Run(() => NativeBinLister.TryEnumerate(targets, effective, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return new RecycleBinContents(collected.Items, collected.Failures, stopwatch.Elapsed);
    }

    /// <summary>Empties the Recycle Bin.</summary>
    /// <param name="volumeRoot">
    /// Volume root to empty, such as <c>"X:\\"</c>, or <see langword="null"/> for every volume that can
    /// hold a Recycle Bin. A path inside a volume is not a volume root and is reported in
    /// <see cref="RecycleBinEmptyResult.Failures"/> rather than emptied as that whole volume.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the sweep between entries. A cancellation part way through raises
    /// <see cref="OperationCanceledException"/> — so a caller who cancels gets no result at all, and what
    /// was removed is found by reading the bin rather than by reading the result.
    /// </param>
    /// <returns>
    /// What was removed and what could not be reached. Naming no volume covers every mounted volume that
    /// can hold a Recycle Bin; a machine with none of those covers nothing, and that is an empty operation
    /// reporting nothing removed and no failures.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<RecycleBinEmptyResult> EmptyAsync(
        string? volumeRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<RecycleBinEmptyResult>(cancellationToken).ConfigureAwait(false);
        }

        var targets = RecycleBinVolumeSet.Resolve(volumeRoot);
        if (targets is null)
        {
            return new RecycleBinEmptyResult(
                [],
                0,
                0,
                [new RecycleFailure(volumeRoot!, VolumeNotFound, 0, RecycleFailureStage.PathValidation)],
                TimeSpan.Zero);
        }

        // Started before the work is queued so that Elapsed covers the wait for a pool thread as well
        // as the empty itself.
        var stopwatch = Stopwatch.StartNew();

        var emptied = await Task
            .Run(() => NativeBinEmptier.Empty(targets, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return emptied with { Elapsed = stopwatch.Elapsed };
    }

    /// <summary>
    /// Restores items from the Recycle Bin to the paths they were deleted from.
    /// </summary>
    /// <param name="items">
    /// The entries to restore, as a listing returned them. An entry whose record cannot be read is a failure
    /// rather than an exception, reported against the entry's own record inside the bin, because a restore
    /// that cannot say where a payload came from has nothing to restore it to.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RestoreOptions.Default"/>.
    /// <see cref="RestoreOptions.DirectoryConflict"/> and <see cref="RestoreOptions.FileConflict"/>
    /// decide what a restore does about a path that is already occupied.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the work between entries and between volumes. A cancelled restore raises without returning a
    /// result: a restore is not transactional, so what was already put back is found by listing the bin
    /// rather than by reading a result.
    /// </param>
    /// <returns>
    /// What every entry became, in the order the entries were passed in whatever order the work itself
    /// finished in. Each entry's payload is moved to the path its record names and its record deleted, so a
    /// restored item leaves the Recycle Bin rather than being copied out of it; a move that fails leaves the
    /// entry whole and restorable, and a delete that fails leaves the file already back where the caller
    /// asked with its entry still in the bin.
    /// <para>
    /// A move the file system refuses is asked again rather than given up on, up to
    /// <see cref="RestoreOptions.MaxAttempts"/> times, under one <see cref="RestoreOptions.OverallTimeout"/>
    /// budget for the whole call. What it is, and a destination somebody lets go of half way through, is
    /// restored; what stays held is reported carrying the attempts it was actually given in
    /// <see cref="RecycleFailure.Attempts"/>.
    /// </para>
    /// <para>
    /// Carries no machine gate, so it is available on a machine too old to write a record. A merge that
    /// cannot place a file of its tree puts that file back into the Recycle Bin with a record of its own,
    /// which is this library's version 2 like any other — so on such a machine that entry is restorable
    /// through this library and not through Explorer's own Restore.
    /// </para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started, or between entries
    /// and between volumes.
    /// </exception>
    public static async Task<RecycleBinRestoreResult> RestoreAsync(
        IReadOnlyList<RecycleBinItem> items,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<RecycleBinRestoreResult>(cancellationToken).ConfigureAwait(false);
        }

        var effective = options ?? RestoreOptions.Default;

        // Started before the work is queued so that Elapsed covers the wait for a pool thread as well as
        // the restore itself.
        var stopwatch = Stopwatch.StartNew();

        var restored = await Task
            .Run(() => RestoreRunner.Run(items, effective, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        return restored.WithElapsed(stopwatch.Elapsed);
    }

    /// <summary>
    /// Restores every entry in every volume group of a listing, to the paths they were deleted from.
    /// </summary>
    /// <param name="volumes">
    /// The groups to restore, as <see cref="RecycleBinContents.Items"/> returned them — or any subset of
    /// them, filtered by the caller. A group holding nothing contributes nothing. A <see langword="null"/>
    /// group is an <see cref="ArgumentException"/> rather than a failure entry: it has no entries to restore
    /// and no volume root to name, so there is no path to report it at and nothing to say which volume could
    /// not be answered.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RestoreOptions.Default"/>.
    /// <see cref="RestoreOptions.DirectoryConflict"/> and <see cref="RestoreOptions.FileConflict"/>
    /// decide what a restore does about a path that is already occupied.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the work between entries and between volumes. A cancelled restore raises without returning a
    /// result: a restore is not transactional, so what was already put back is found by listing the bin
    /// rather than by reading a result.
    /// </param>
    /// <returns>
    /// What every entry became, in the order the groups were passed in and the entries sat inside them. The
    /// groups are restored as one batch, so the answer of a group of fifty volumes is the answer of the
    /// fifty thousand entries they hold rather than fifty answers. An entry named twice is restored once and
    /// reported, because the group it arrived in does not decide which volume it came from.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="volumes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// One of the groups is <see langword="null"/>. Nothing was attempted and every other group was left
    /// alone, because the argument is settled before any entry is read.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started, or between entries
    /// and between volumes.
    /// </exception>
    public static async Task<RecycleBinRestoreResult> RestoreAsync(
        IReadOnlyList<RecycleBinVolumeContents> volumes,
        RestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        if (volumes.Any(volume => volume is null))
        {
            throw new ArgumentException(
                "A volume group was null. A group that is not there has no entries to restore and no volume root to name.",
                nameof(volumes));
        }

        return await RestoreAsync(
                RecycleBinVolumeContents.Flatten(volumes),
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reports whether the Recycle Bin is in use for a volume, and why.
    /// </summary>
    /// <param name="volumeRoot">Volume root to inspect, such as <c>"X:\\"</c>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The status of that volume. A volume whose own setting was never recorded answers
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/>: unproven, never enabled, and refused
    /// under the default <see cref="NoRecycleBinAction.Throw"/>.
    /// </returns>
    /// <remarks>
    /// A configuration answer is not a prediction. A volume mounted after the call, or a policy applied
    /// after it, changes the answer without this call having been wrong. Nothing is cached, so each call
    /// asks the question again.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="volumeRoot"/> is null, empty or whitespace.</exception>
    public static Task<RecycleBinStatus> GetStatusAsync(
        string volumeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(RecycleBinVolumeState.Inspect(volumeRoot));
    }
}

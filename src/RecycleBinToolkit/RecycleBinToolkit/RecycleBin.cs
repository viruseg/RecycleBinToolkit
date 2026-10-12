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
    /// <param name="filePath">Path of the file or directory.</param>
    /// <param name="options">
    /// <see langword="null"/> means <see cref="RecycleOptions.Default"/>. This is the overload
    /// <see cref="RecycleOptions.ThrowOnError"/> is read on.
    /// </param>
    /// <param name="cancellationToken">Cancels the validation scan and the work.</param>
    /// <returns>
    /// <see langword="true"/> when the path is gone from disk, whether it was recycled or deleted
    /// permanently under an explicit policy.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is null, empty or whitespace and
    /// <see cref="RecycleOptions.ThrowOnError"/> is on.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist and <see cref="RecycleOptions.ThrowOnError"/> is on.
    /// </exception>
    /// <exception cref="IOException">
    /// A move failed, or a volume refused the path, and <see cref="RecycleOptions.ThrowOnError"/> is on.
    /// The file is untouched in every one of those cases.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The path names its volume by GUID and <see cref="RecycleOptions.ThrowOnError"/> is on. Nothing was
    /// attempted; address the same location by drive letter or mount point instead.
    /// </exception>
    /// <exception cref="PlatformNotSupportedException">
    /// This machine's Recycle Bin cannot be written to (see Requirements in the README). Nothing was
    /// attempted, and <see cref="RecycleOptions.ThrowOnError"/> does not suppress it.
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
    /// entry is reported as a failure without aborting the rest of the batch. A repeated path is recycled
    /// once.
    /// </param>
    /// <param name="options">
    /// <see langword="null"/> means <see cref="RecycleOptions.Default"/>.
    /// <see cref="RecycleOptions.ThrowOnError"/> is not read here: every per-item failure is reported
    /// through <see cref="RecycleBinResult.Failures"/> instead of raising.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the validation scan and the work between paths. A cancelled batch raises and returns no
    /// result: what was recycled is found by reading the bin.
    /// </param>
    /// <returns>
    /// The outcome of the batch, naming every path that was not handled in
    /// <see cref="RecycleBinResult.Failures"/>. A path refused because its volume cannot recycle it
    /// arrives there at stage <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/>, and the paths on
    /// every other volume in the batch are still recycled.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
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

        // Validating a batch is one filesystem probe per path, and it is slow enough that running it inline
        // would be the caller's own time: an async method runs everything before its first await on the
        // thread that called it, so a caller on a UI thread would be frozen for the whole scan and a
        // caller holding a lock would hold it. Hopping to the pool first costs a scheduling round trip
        // and buys back the caller's thread.
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
    /// Volume root to read, such as <c>"X:\"</c>, or <see langword="null"/> for every volume. A path
    /// inside a volume is not a volume root: it is refused at stage
    /// <see cref="RecycleFailureStage.PathValidation"/> rather than answered for the whole volume
    /// containing it.
    /// </param>
    /// <param name="options">
    /// <see langword="null"/> means <see cref="GetItemsOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The entries that could be read, grouped by volume in the order the volumes were asked about, and
    /// the ones that could not in <see cref="RecycleBinContents.Failures"/>. An environmental problem is
    /// data in the result, not an exception.
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
    /// Volume root to empty, such as <c>"X:\"</c>, or <see langword="null"/> for every mounted volume
    /// that can hold a Recycle Bin. A path inside a volume is not a volume root and is reported in
    /// <see cref="RecycleBinEmptyResult.Failures"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the sweep between entries. A cancellation part way through raises: the caller gets no
    /// result, and what was removed is found by reading the bin.
    /// </param>
    /// <returns>What was removed and what could not be reached.</returns>
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

    /// <summary>Restores items from the Recycle Bin to the paths they were deleted from.</summary>
    /// <param name="items">
    /// The entries to restore, as a listing returned them. An entry whose record cannot be read is a
    /// failure rather than an exception, reported against the entry's own record inside the bin.
    /// </param>
    /// <param name="options">
    /// <see langword="null"/> means <see cref="RestoreOptions.Default"/>.
    /// <see cref="RestoreOptions.FileConflict"/> and <see cref="RestoreOptions.DirectoryConflict"/>
    /// decide what a restore does about an occupied path.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the work between entries. A cancelled restore raises without returning a result: what was
    /// already put back is found by listing the bin.
    /// </param>
    /// <returns>
    /// What every entry became, in the order the entries were passed in. A move the file system refuses is
    /// tried again up to <see cref="RestoreOptions.MaxAttempts"/> times, under one
    /// <see cref="RestoreOptions.OverallTimeout"/> budget for the whole call; what stays held is reported
    /// carrying the attempts it was given in <see cref="RecycleFailure.Attempts"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
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

    /// <summary>Restores every entry in every volume group of a listing.</summary>
    /// <param name="volumes">
    /// The groups to restore, as <see cref="RecycleBinContents.Items"/> returned them, or any subset of
    /// them. A group holding nothing contributes nothing. An entry named twice is restored once and
    /// reported.
    /// </param>
    /// <param name="options">
    /// <see langword="null"/> means <see cref="RestoreOptions.Default"/>.
    /// <see cref="RestoreOptions.FileConflict"/> and <see cref="RestoreOptions.DirectoryConflict"/>
    /// decide what a restore does about an occupied path.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the work between entries and between volumes. A cancelled restore raises without returning
    /// a result: what was already put back is found by listing the bin.
    /// </param>
    /// <returns>
    /// What every entry became, in the order the groups were passed in and the entries sat inside them. The
    /// groups are restored as one batch.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="volumes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// One of the groups is <see langword="null"/>. It has no entries to restore and no volume root to
    /// name, so there is no path to report it at. Nothing was attempted and every other group was left
    /// alone.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
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

    /// <summary>Reports whether the Recycle Bin is in use for a volume.</summary>
    /// <param name="volumeRoot">Volume root to inspect, such as <c>"X:\"</c>.</param>
    /// <param name="cancellationToken">Checked before the work starts.</param>
    /// <returns>
    /// The status of that volume. A volume whose own setting was never recorded answers
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/>, and is refused under the default
    /// <see cref="NoRecycleBinAction.Throw"/>.
    /// </returns>
    /// <remarks>The answer is not cached: a change made after the call is seen by the next one.</remarks>
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

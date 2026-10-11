using System.Diagnostics;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves files and directories into the Windows Recycle Bin, and reads, empties and reports on the bin
/// itself.
/// </summary>
/// <remarks>
/// The single entry point to this library. Safe to call from any thread. Work runs on the thread pool, so
/// a caller never blocks inside the bin.
/// <para>
/// A path whose volume cannot recycle it is settled before anything is attempted, so it is never
/// removed for good while the operation reports it as recycled. What happens to it instead is
/// <see cref="RecycleBinOptions.NoRecycleBinAction"/>: left where it is, recycled anyway, or removed
/// by this library and named in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static class RecycleBin
{
    /// <summary>Win32 <c>ERROR_PATH_NOT_FOUND</c>, for a path that is not a volume root.</summary>
    private const int VolumeNotFound = unchecked((int)0x800703FE);

    /// <summary>
    /// Recycles a single file or directory.
    /// </summary>
    /// <param name="filePath">Path of the file or directory to recycle.</param>
    /// <param name="options">
    /// Retry, verification and error-handling settings. <see langword="null"/> means
    /// <see cref="RecycleBinOptions.Default"/>. <see cref="RecycleBinOptions.ThrowOnError"/> decides
    /// whether a failure raises or is returned as <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the scan that validates the batch and the work between paths. A cancelled batch stops
    /// part way and raises without returning a result: which files went is found by reading the bin.
    /// The validation scan is checked too, because on a large batch it is the long half of the call
    /// and it is over before the first move would have begun.
    /// </param>
    /// <returns><see langword="true"/> when the path was removed from disk.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is null, empty or whitespace and
    /// <see cref="RecycleBinOptions.ThrowOnError"/> is on.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist and <see cref="RecycleBinOptions.ThrowOnError"/> is
    /// on.
    /// </exception>
    /// <exception cref="IOException">
    /// The move failed, or the volume holding the path cannot recycle it and
    /// <see cref="RecycleBinOptions.NoRecycleBinAction"/> is <see cref="NoRecycleBinAction.Throw"/>, or
    /// that action is <see cref="NoRecycleBinAction.DeletePermanently"/> and the file system refused
    /// the removal — and <see cref="RecycleBinOptions.ThrowOnError"/> is on. The file is left in place
    /// in every one of those cases. <see cref="Exception.HResult"/> carries the file system's own code,
    /// or <c>0</c> where the volume was refused from configuration.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The path names its volume by GUID, which this library cannot measure,
    /// and <see cref="RecycleBinOptions.ThrowOnError"/> is on. Nothing was attempted and the file is
    /// where it was left. A path in that form has no other spelling, so address the same location by drive
    /// letter or mount point instead.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<bool> RecycleAsync(
        string? filePath,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? RecycleBinOptions.Default;

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

    /// <summary>
    /// Recycles a batch of files or directories.
    /// </summary>
    /// <param name="filePaths">
    /// Paths to recycle. Entries may be <see langword="null"/> or refer to paths that do not exist;
    /// each such entry is reported as a failure without aborting the rest of the batch. Repeated
    /// paths are recycled once.
    /// </param>
    /// <param name="options">
    /// Retry, verification and error-handling settings. <see langword="null"/> means
    /// <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the scan that validates the batch and the work between paths. A cancelled batch stops
    /// part way and raises without returning a result: which files went is found by reading the bin.
    /// The validation scan is checked too, because on a large batch it is the long half of the call
    /// and it is over before the first move would have begun.
    /// </param>
    /// <returns>
    /// The outcome of the batch. Never throws for a per-item failure; read
    /// <see cref="RecycleBinResult.Failures"/> for the diagnostics. A path refused because its volume
    /// cannot recycle it arrives there at stage <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/>,
    /// and the paths on every other volume in the batch are still recycled.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, whether before the work started or while the
    /// batch was being validated.
    /// </exception>
    public static async Task<RecycleBinResult> RecycleAsync(
        IReadOnlyList<string?> filePaths,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<RecycleBinResult>(cancellationToken).ConfigureAwait(false);
        }

        var effective = options ?? RecycleBinOptions.Default;

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
    /// <remarks>
    /// The mapping from a stage to an exception is this overload's own documented contract rather than a
    /// throw site of its own, which is why it sits here and not in <see cref="RecycleBinThrowHelper"/>:
    /// it constructs nothing, it chooses which of that class's failures to raise.
    /// </remarks>
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

    /// <summary>
    /// Reads the contents of the Recycle Bin.
    /// </summary>
    /// <param name="volumeRoot">
    /// Volume root to keep, such as <c>"X:\\"</c>, or <see langword="null"/> for every volume. A path inside
    /// a volume is not a volume root: it is refused with a <see cref="RecycleFailureStage.PathValidation"/>
    /// failure rather than answered for the whole volume containing it.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The entries that could be read, plus the ones that could not.</returns>
    /// <remarks>
    /// Each volume's bin is enumerated directly, so a valid <paramref name="volumeRoot"/> selects which
    /// directories to read. Entries that could not be described arrive in
    /// <see cref="RecycleBinContents.Failures"/>; an environmental problem is never an exception.
    /// <para>
    /// A root that names no volume is answered as a failure rather than as an empty result: an empty
    /// listing is a claim about the bin, and the caller whose root was one character wrong cannot tell
    /// that from a bin that really is empty.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<RecycleBinContents> GetItemsAsync(
        string? volumeRoot = null,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? RecycleBinOptions.Default;
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

    /// <summary>
    /// Empties the Recycle Bin.
    /// </summary>
    /// <param name="volumeRoot">
    /// Volume root to empty, such as <c>"X:\\"</c>, or <see langword="null"/> for every volume. A path
    /// inside a volume is not a volume root and is reported in
    /// <see cref="RecycleBinEmptyResult.Failures"/> rather than emptied as that whole volume.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the sweep. The sweep checks the token between entries and a cancellation part way through
    /// raises <see cref="OperationCanceledException"/> — so a caller who cancels gets no result at all,
    /// and what was removed is found by reading the bin rather than by reading the result.
    /// </param>
    /// <returns>What was removed, and what could not be reached.</returns>
    /// <remarks>
    /// Naming no volume covers every mounted volume that can hold a Recycle Bin. A machine with none of
    /// those — no fixed or removable drive that is ready — covers nothing, and that is an empty operation
    /// that reports nothing removed and no failures rather than a request that reaches every volume on the
    /// machine.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<RecycleBinEmptyResult> EmptyAsync(
        string? volumeRoot = null,
        RecycleBinOptions? options = null,
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
    /// The entries to restore, as a listing returned them. Each is an object a listing produced, so an entry
    /// whose record cannot be read raises rather than being described as a failure — the entry's own
    /// <see cref="RecycleBinItem.OriginalPath"/> says so, and a restore that cannot say where a payload came
    /// from has nothing to restore it to.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>.
    /// <see cref="RecycleBinOptions.DirectoryConflict"/> and <see cref="RecycleBinOptions.FileConflict"/>
    /// decide what a restore does about a path that is already occupied.
    /// </param>
    /// <param name="cancellationToken">Cancels the work between entries.</param>
    /// <returns>
    /// What every entry became, in the order the entries were passed in whatever order the work itself
    /// finished in.
    /// </returns>
    /// <remarks>
    /// An entry's payload is moved back to the path its record names and its record is then deleted, so a
    /// restored item leaves the Recycle Bin rather than being copied out of it.
    /// <para>
    /// Nothing here writes an index record, which is why this carries none of the machine gate
    /// <see cref="RecycleAsync(IReadOnlyList{string?}, RecycleBinOptions?, CancellationToken)"/> does: that
    /// one leaves records the Explorer's own Restore has to be able to read, and this one only takes them
    /// away.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started, or between entries.
    /// </exception>
    /// <exception cref="IOException">
    /// An entry's payload would not move, or its record would not go. The payload's own file system code is
    /// in <see cref="Exception.HResult"/>. Under the order the engine restores in, a move that failed leaves
    /// the entry whole and restorable; a delete that failed leaves the file already back where its caller
    /// asked for it, with its entry still in the bin.
    /// </exception>
    public static async Task<RecycleBinRestoreResult> RestoreAsync(
        IReadOnlyList<RecycleBinItem> items,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<RecycleBinRestoreResult>(cancellationToken).ConfigureAwait(false);
        }

        var effective = options ?? RecycleBinOptions.Default;

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
    /// them, filtered by the caller. A group holding nothing contributes nothing. A
    /// <see langword="null"/> group is an <see cref="ArgumentException"/> rather than a failure entry: it has
    /// no entries to restore and no volume root to name, so there is no path to report it at and nothing to
    /// say which volume could not be answered.
    /// </param>
    /// <param name="options">
    /// Settings. <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>.
    /// <see cref="RecycleBinOptions.DirectoryConflict"/> and <see cref="RecycleBinOptions.FileConflict"/>
    /// decide what a restore does about a path that is already occupied.
    /// </param>
    /// <param name="cancellationToken">Cancels the work between entries.</param>
    /// <returns>
    /// What every entry became, in the order the groups were passed in and the entries sat inside them.
    /// </returns>
    /// <remarks>
    /// The groups are flattened and handed to the entry overload, so the answer of a group of fifty volumes
    /// is the answer of the fifty thousand entries they hold rather than fifty answers. Which volume an entry
    /// came from is settled by its own record path and not by the group it arrived in, so a caller may hand
    /// the same entry in more than once and it is restored once, as it would be in a flat list.
    /// <para>
    /// Everything else is as
    /// <see cref="RestoreAsync(IReadOnlyList{RecycleBinItem}, RecycleBinOptions?, CancellationToken)"/>
    /// promises: each entry's outcome is reported on its own, the elapsed time covers the wait, and no
    /// machine gate applies because nothing here writes an index record.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="volumes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// One of the groups is <see langword="null"/>. Nothing was attempted and every other group was left
    /// alone, because the argument is settled before any entry is read.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started, or between entries.
    /// </exception>
    /// <exception cref="IOException">
    /// An entry's payload would not move, or its record would not go — see the entry overload for what
    /// each of those leaves behind.
    /// </exception>
    public static async Task<RecycleBinRestoreResult> RestoreAsync(
        IReadOnlyList<RecycleBinVolumeContents> volumes,
        RecycleBinOptions? options = null,
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
    /// <param name="options">
    /// Accepted for uniformity with the other methods and not used. This one answers what a volume's
    /// configuration says, not how items are moved.
    /// <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The status of that volume.</returns>
    /// <remarks>
    /// This answers what configuration says about the volume. For a volume whose per-volume setting was
    /// not recorded — either because the volume API would not resolve its GUID or because Windows has
    /// never configured a bin for it — the answer is
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/>: not proven enabled, and refused under
    /// the default <see cref="NoRecycleBinAction.Throw"/>. It does not predict the next delete: a volume
    /// mounted after this call, or a policy applied after it, changes the answer without this call being
    /// wrong.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="volumeRoot"/> is null, empty or whitespace.</exception>
    public static Task<RecycleBinStatus> GetStatusAsync(
        string volumeRoot,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        cancellationToken.ThrowIfCancellationRequested();

        _ = options;

        return Task.FromResult(RecycleBinVolumeState.Inspect(volumeRoot));
    }
}

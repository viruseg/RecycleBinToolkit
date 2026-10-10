using System.Diagnostics;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Reads, empties and reports on the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// Safe to call from any thread. Work runs on the thread pool, so a caller never blocks inside the bin.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static class RecycleBin
{
    /// <summary>Win32 <c>ERROR_PATH_NOT_FOUND</c>, for a path that is not a volume root.</summary>
    private const int VolumeNotFound = unchecked((int)0x800703FE);

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

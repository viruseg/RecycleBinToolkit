using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The Recycle Bin through the filesystem rather than through the Shell: index records and payloads under
/// <c>$RECYCLE.BIN</c>, moved and read with ordinary file calls.
/// </summary>
/// <remarks>
/// Everything here blocks, so every entry point hands the work to the thread pool rather than running it on
/// whichever thread called. Nothing here reaches <see cref="RecycleBinWorker"/>, and that is the point of
/// the seam: this backend is the reason a caller can use this library from a process with no single-
/// threaded apartment at all.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class NativeRecycleBinBackend : IRecycleBinBackend
{
    private static readonly NativeRecycleBinBackend Shared = new();

    private NativeRecycleBinBackend()
    {
    }

    internal static NativeRecycleBinBackend Instance => Shared;

    /// <summary>
    /// Moves a batch on a pool thread.
    /// </summary>
    /// <param name="targets">Paths to recycle.</param>
    /// <param name="options">Retry and verification settings.</param>
    /// <param name="cancellationToken">Cancels the work between paths.</param>
    /// <returns>The outcome of the batch.</returns>
    public Task<RecycleBinResult> RecycleAsync(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken) =>
        Task.Run(() => Recycle(targets, options, cancellationToken), cancellationToken);

    /// <summary>
    /// Moves a batch, on whatever thread it is given.
    /// </summary>
    /// <param name="targets">Paths to recycle.</param>
    /// <param name="options">Retry and verification settings.</param>
    /// <param name="cancellationToken">Cancels the work between paths.</param>
    /// <returns>The outcome of the batch.</returns>
    /// <remarks>
    /// Separate from <see cref="RecycleAsync"/> so a caller that already left its own thread — because
    /// validating a batch of twenty thousand paths takes minutes on the thread it would otherwise block —
    /// can have the validation and the recycle share one trip to the pool rather than two.
    /// </remarks>
    internal static RecycleBinResult Recycle(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken) =>
        NativeRecycleRunner.Run(targets, options, cancellationToken);

    /// <summary>
    /// Reads the bins on a pool thread.
    /// </summary>
    /// <param name="volumeRoots">Volume roots to read.</param>
    /// <param name="options">Settings already normalised; the preload flag decides whether records are read here.</param>
    /// <param name="cancellationToken">Cancels the enumeration and the reading.</param>
    /// <returns>The entries that could be described, plus one failure per entry that could not be.</returns>
    public Task<RecycleBinContents> ListAsync(
        IReadOnlyList<string> volumeRoots,
        RecycleBinOptions options,
        CancellationToken cancellationToken) =>
        Task.Run(() => NativeBinLister.TryEnumerate(volumeRoots, options, cancellationToken), cancellationToken);

    /// <summary>
    /// Empties the bins on a pool thread.
    /// </summary>
    /// <param name="volumeRoots">Volume roots to empty.</param>
    /// <param name="cancellationToken">Cancels the sweep between entries.</param>
    /// <returns>What was removed, and what could not be reached.</returns>
    public Task<RecycleBinEmptyResult> EmptyAsync(IReadOnlyList<string> volumeRoots, CancellationToken cancellationToken) =>
        Task.Run(() => NativeBinEmptier.Empty(volumeRoots, cancellationToken), cancellationToken);
}
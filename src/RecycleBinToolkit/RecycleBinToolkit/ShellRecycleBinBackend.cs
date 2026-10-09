using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The Recycle Bin operations as this library has always done them: the Shell, on the STA worker.
/// </summary>
/// <remarks>
/// This is where the Shell path lives so that it is the only thing that reaches
/// <see cref="RecycleBinWorker"/>. The bodies are the ones <see cref="RecycleBin"/> carried, moved without
/// change — the worker, the retry rounds, the per-item diagnostics and every comment explaining why they
/// are the way they are.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class ShellRecycleBinBackend : IRecycleBinBackend
{
    private static readonly ShellRecycleBinBackend Shared = new();

    private ShellRecycleBinBackend()
    {
    }

    /// <summary>The one instance. The backend holds no state, so there is nothing to have more than one of.</summary>
    internal static ShellRecycleBinBackend Instance => Shared;

    /// <summary>
    /// Hands a batch to the Shell worker and completes when the Shell has finished with it.
    /// </summary>
    /// <param name="targets">Paths to move to the Recycle Bin.</param>
    /// <param name="options">Retry and verification settings.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait. Cancellation never tears the worker down: a request already handed to the
    /// Shell runs to completion, so a cancelled caller cannot leave a half-performed operation.
    /// </param>
    /// <returns>The outcome of the batch.</returns>
    public Task<RecycleBinResult> RecycleAsync(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken) =>
        RecycleBinWorker.SubmitAsync(targets, options.Normalize(), cancellationToken);

    /// <summary>
    /// Reads the whole Recycle Bin through the Shell's virtual folder, on the worker thread.
    /// </summary>
    /// <param name="volumeRoots">
    /// The volumes the operation covers. The Shell's folder aggregates every mounted volume regardless, so
    /// this narrows an enumeration that has already happened rather than making a cheaper query; the
    /// filtering happens in <see cref="RecycleBin"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The entries that could be read, plus the ones that could not.</returns>
    public Task<RecycleBinContents> ListAsync(IReadOnlyList<string> volumeRoots, CancellationToken cancellationToken) =>
        RecycleBinWorker.RunAsync(Collect, cancellationToken);

    /// <summary>
    /// Enumerates the Recycle Bin on the worker thread, where the Shell apartment already is.
    /// </summary>
    /// <remarks>
    /// The elapsed time is not measured here. This runs on the Shell worker, so a stopwatch started here
    /// would report the Shell's share of the work and omit how long the caller waited for the worker to be
    /// free — which for a library that queues every operation behind one thread is most of what a caller
    /// experiences. <see cref="RecycleBin.GetItemsAsync"/> and <see cref="RecycleBin.EmptyAsync"/> measure
    /// around the wait instead, the same way, so both results mean the same thing.
    /// </remarks>
    private static RecycleBinContents Collect()
    {
        var items = new List<RecycleBinItem>();
        var failures = new List<RecycleFailure>();

        RecycleBinLister.TryEnumerate(items, failures);

        return new RecycleBinContents(items, failures, TimeSpan.Zero);
    }
}
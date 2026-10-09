using System.Runtime.Versioning;
using Windows.Win32;

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
    /// <summary>
    /// Ask for nothing, progress nothing, play nothing. An unattended library must never put a dialog
    /// or a sound on a caller's desktop.
    /// </summary>
    private const uint EmptyFlags = PInvoke.SHERB_NOCONFIRMATION | PInvoke.SHERB_NOPROGRESSUI | PInvoke.SHERB_NOSOUND;

    /// <summary>Win32 <c>ERROR_NOT_SET</c>, used when the Shell gives no error code of its own.</summary>
    private const int UnreadableVolume = unchecked((int)0x80070490);

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

    /// <summary>
    /// Empties the targets on the worker thread and reports what went, measured rather than assumed.
    /// </summary>
    /// <param name="targets">Volume roots to empty.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>What was removed, and what could not be reached.</returns>
    /// <remarks>
    /// An empty handed to the Shell always finishes: it is one call that removes everything, and stopping
    /// it part way would leave the caller with a bin neither full nor empty and no way to know which.
    /// </remarks>
    public Task<RecycleBinEmptyResult> EmptyAsync(IReadOnlyList<string> targets, CancellationToken cancellationToken) =>
        RecycleBinWorker.RunAsync(() => Empty(targets), cancellationToken);

    /// <summary>
    /// Removes everything from the targets.
    /// </summary>
    private static RecycleBinEmptyResult Empty(IReadOnlyList<string> targets)
    {
        var failures = new List<RecycleFailure>();
        var emptied = new List<string>(targets.Count);

        // Nothing is measured and nothing is asked. A machine covering no volume has no Recycle Bin of its
        // own to empty, and handing the Shell a null root would empty every volume's regardless.
        if (!RecycleBin.TryResolveShellRoot(targets, out var shellRoot))
        {
            return new RecycleBinEmptyResult(emptied, 0, 0, failures, TimeSpan.Zero);
        }

        // Measured on both sides of the call, per volume, because the Shell reports success whether it
        // removed ten entries or none and says nothing about either count.
        var itemsBefore = new long?[targets.Count];
        var bytesBefore = new long?[targets.Count];

        for (var i = 0; i < targets.Count; i++)
        {
            if (RecycleBinCounter.TryQuery(targets[i], out var items, out var bytes))
            {
                itemsBefore[i] = items;
                bytesBefore[i] = bytes;
            }
        }

        var hrEmpty = PInvoke.SHEmptyRecycleBin(default, shellRoot, EmptyFlags);

        long itemsRemoved = 0;
        long bytesFreed = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            if (!itemsBefore[i].HasValue || !RecycleBinCounter.TryQuery(targets[i], out var itemsAfter, out var bytesAfter))
            {
                failures.Add(new RecycleFailure(targets[i], UnreadableVolume, 1, RecycleFailureStage.Enumeration));
                continue;
            }

            // A Recycle Bin that grew while the operation ran would otherwise report a negative
            // removal, which no caller can interpret.
            var removed = Math.Max(0, itemsBefore[i]!.Value - itemsAfter);
            var freed = Math.Max(0, bytesBefore[i]!.Value - bytesAfter);

            // The Shell's verdict is only consulted where the measurement cannot speak for itself:
            // it reports an error when asked to empty a Recycle Bin that is already empty, so an
            // HRESULT alone would fail a call that did exactly what was asked. A volume counts as
            // failed only when the Shell refused and something is demonstrably still there.
            if (hrEmpty.Failed && removed == 0 && itemsAfter > 0)
            {
                failures.Add(new RecycleFailure(targets[i], hrEmpty.Value, 1, RecycleFailureStage.Performing));
                continue;
            }

            itemsRemoved += removed;
            bytesFreed += freed;
            emptied.Add(targets[i]);
        }

        return new RecycleBinEmptyResult(emptied, itemsRemoved, bytesFreed, failures, TimeSpan.Zero);
    }
}

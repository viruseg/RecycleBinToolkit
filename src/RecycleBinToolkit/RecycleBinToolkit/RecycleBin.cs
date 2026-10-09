using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace RecycleBinToolkit;

/// <summary>
/// Reads, empties and reports on the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// Safe to call from any thread, including one that is already a single-threaded apartment. Shell
/// work runs on the same worker thread as <see cref="FileRecycler"/>, so a caller never pays for COM
/// initialisation and every COM call in the process stays on one thread.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static class RecycleBin
{
    /// <summary>
    /// Ask for nothing, progress nothing, play nothing. An unattended library must never put a dialog
    /// or a sound on a caller's desktop.
    /// </summary>
    private const uint EmptyFlags = PInvoke.SHERB_NOCONFIRMATION | PInvoke.SHERB_NOPROGRESSUI | PInvoke.SHERB_NOSOUND;

    /// <summary>Win32 <c>ERROR_NOT_SET</c>, used when the Shell gives no error code of its own.</summary>
    private const int UnreadableVolume = unchecked((int)0x80070490);

    /// <summary>Win32 <c>ERROR_PATH_NOT_FOUND</c>, for a path that is not a volume root.</summary>
    private const int VolumeNotFound = unchecked((int)0x800703FE);

    /// <summary>
    /// Reads the contents of the Recycle Bin.
    /// </summary>
    /// <param name="volumeRoot">
    /// Volume root to keep, such as <c>"X:\\"</c>, or <see langword="null"/> for every volume.
    /// </param>
    /// <param name="options">
    /// Which backend to read through. <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>,
    /// which is <see cref="RecycleBinBackend.WinApi"/>. The native backend reads the same
    /// <c>$RECYCLE.BIN</c> directories and returns the same entries.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The entries that could be read, plus the ones that could not.</returns>
    /// <remarks>
    /// The Shell's Recycle Bin is a single virtual folder spanning every mounted volume, so on
    /// <see cref="RecycleBinBackend.WinApi"/> <paramref name="volumeRoot"/> narrows an enumeration
    /// that has already happened rather than making a cheaper query. Entries that could not be
    /// described arrive in <see cref="RecycleBinContents.Failures"/> on either backend; an
    /// environmental problem is never an exception.
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

        var collected = await RecycleBinBackends
            .For(effective)
            .ListAsync(RecycleBinVolumeSet.Resolve(volumeRoot) ?? [], cancellationToken)
            .ConfigureAwait(false);

        var items = Filter(collected.Items, volumeRoot);

        return new RecycleBinContents(items, collected.Failures, stopwatch.Elapsed);
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
    /// Which backend to empty through. <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>,
    /// which is <see cref="RecycleBinBackend.WinApi"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait. On <see cref="RecycleBinBackend.WinApi"/> an empty already handed to the
    /// Shell always finishes.
    /// </param>
    /// <returns>What was removed, and what could not be reached.</returns>
    /// <remarks>
    /// Naming no volume covers every mounted volume that can hold a Recycle Bin. A machine with none of
    /// those — no fixed or removable drive that is ready — covers nothing, and that is an empty operation
    /// that reports nothing removed and no failures rather than a request that reaches every volume on the
    /// machine. The two are told apart before the Shell is asked, not after.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<RecycleBinEmptyResult> EmptyAsync(
        string? volumeRoot = null,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _ = options;
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

        // Started before the work is queued so that Elapsed covers the wait for the Shell worker as well
        // as the empty itself. Every operation in this library queues behind one thread, so time spent
        // waiting for it is part of what the caller waited, and reporting only the Shell's share would
        // understate it by however long the worker was busy with something else.
        var stopwatch = Stopwatch.StartNew();

        var emptied = await RecycleBinWorker
            .RunAsync(() => Empty(targets), cancellationToken)
            .ConfigureAwait(false);

        return emptied with { Elapsed = stopwatch.Elapsed };
    }

    /// <summary>
    /// Decides the root the Shell is handed, which is the only place that decides it.
    /// </summary>
    /// <param name="targets">The volume roots the operation covers, each measured before and after.</param>
    /// <param name="shellRoot">
    /// The one volume to ask about, or <see langword="null"/> to ask about every volume the operation
    /// covers. Set only when <paramref name="targets"/> is non-empty.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when there is something to empty. <see langword="false"/> when there is
    /// nothing — the Shell is not to be asked at all.
    /// </returns>
    /// <remarks>
    /// <c>SHEmptyRecycleBin</c> reads a null root as every volume on the machine, so the two cases mean
    /// very different things and only one of them is ever safe to pass:
    /// <list type="bullet">
    /// <item>Nothing to empty. A machine whose fixed and removable drives are all gone — or one that
    /// never had any — covers no volume, and asking about "all" of them there empties every Recycle Bin
    /// on it while the loop measuring before and after never runs once. The result would report nothing
    /// removed and no failures, which is the whole class of outcome this library exists to refuse.</item>
    /// <item>One volume. The root handed over is that volume, which is what makes the counts in the
    /// result describe the empty that actually happened.</item>
    /// <item>Several volumes. Every one of them was measured, so "all" means exactly the set the
    /// operation covers and nothing beyond it.</item>
    /// </list>
    /// <para>
    /// Deriving the root here rather than in the caller is what makes the empty set unreachable by
    /// accident: the value that asks about every volume exists only where the set of measured volumes
    /// is in hand.
    /// </para>
    /// </remarks>
    internal static bool TryResolveShellRoot(IReadOnlyList<string> targets, out string? shellRoot)
    {
        switch (targets.Count)
        {
            case 0:
                shellRoot = null;
                return false;

            case 1:
                shellRoot = targets[0];
                return true;

            default:
                shellRoot = null;
                return true;
        }
    }

    /// <summary>
    /// Reports whether the Recycle Bin is in use for a volume, and why.
    /// </summary>
    /// <param name="volumeRoot">Volume root to inspect, such as <c>"X:\\"</c>.</param>
    /// <param name="options">
    /// Accepted for uniformity with the other methods and not used. This one answers what a volume's
    /// configuration says, not how items are moved, so its answer is the same on either backend.
    /// <see langword="null"/> means <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The status of that volume.</returns>
    /// <remarks>
    /// This answers what configuration says about the volume, and — for a volume whose per-volume
    /// setting was not recorded, either because the volume API would not resolve its GUID or because
    /// Windows has never configured a bin for it — whether the Shell reports a Recycle Bin on it. It does
    /// not predict the next delete: a volume mounted after this call, or a policy applied after it,
    /// changes the answer without this call being wrong.
    /// <para>
    /// It runs on the calling thread rather than on the Shell worker, so it never blocks behind an
    /// in-flight batch. Asking the Shell is the slow half of the work, and it happens only for that same
    /// volume, where the question walks the bin's metadata.
    /// </para>
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

    /// <summary>
    /// Removes everything from the targets and reports what went, measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// The elapsed time is not measured here. This runs on the Shell worker, so a stopwatch started here
    /// would report the Shell's share of the work and omit how long the caller waited for the worker to be
    /// free — which for a library that queues every operation behind one thread is most of what a caller
    /// experiences. <see cref="EmptyAsync"/> measures around the wait instead, the same way
    /// <see cref="GetItemsAsync"/> does, so both results mean the same thing.
    /// </remarks>
    private static RecycleBinEmptyResult Empty(List<string> targets)
    {
        var failures = new List<RecycleFailure>();
        var emptied = new List<string>(targets.Count);

        // Nothing is measured and nothing is asked. A machine covering no volume has no Recycle Bin of its
        // own to empty, and handing the Shell a null root would empty every volume's regardless.
        if (!TryResolveShellRoot(targets, out var shellRoot))
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

    /// <summary>
    /// Keeps only the entries that were deleted from <paramref name="volumeRoot"/>.
    /// </summary>
    /// <remarks>
    /// The Recycle Bin is one aggregate folder spanning every mounted volume, so this is a filter over
    /// a complete enumeration. The root is normalised first, so the comparison below is a prefix test
    /// against a real volume root with its separator, whichever spelling the caller used.
    /// </remarks>
    private static IReadOnlyList<RecycleBinItem> Filter(IReadOnlyList<RecycleBinItem> items, string? volumeRoot)
    {
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return items;
        }

        var root = NormalizeRoot(volumeRoot);

        var kept = new List<RecycleBinItem>(items.Count);
        foreach (var item in items)
        {
            if (item.OriginalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(item);
            }
        }

        return kept;
    }

    private static string NormalizeRoot(string volumeRoot) =>
        RecycleBinVolumeState.NormalizeVolumeRoot(volumeRoot);
}

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
    /// Volume root to keep, such as <c>"E:\\"</c>, or <see langword="null"/> for every volume.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The entries that could be read, plus the ones that could not.</returns>
    /// <remarks>
    /// The Recycle Bin is a single Shell folder spanning every mounted volume, so
    /// <paramref name="volumeRoot"/> narrows an enumeration that has already happened rather than
    /// making a cheaper query. Entries the Shell would not describe arrive in
    /// <see cref="RecycleBinContents.Failures"/>; an environmental problem is never an exception.
    /// </remarks>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    public static async Task<RecycleBinContents> GetItemsAsync(
        string? volumeRoot = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var collected = await RecycleBinWorker
            .RunAsync(Collect, cancellationToken)
            .ConfigureAwait(false);

        var items = Filter(collected.Items, volumeRoot);

        return new RecycleBinContents(items, collected.Failures, stopwatch.Elapsed);
    }

    /// <summary>
    /// Empties the Recycle Bin.
    /// </summary>
    /// <param name="volumeRoot">
    /// Volume root to empty, such as <c>"E:\\"</c>, or <see langword="null"/> for every volume. A path
    /// inside a volume is not a volume root and is reported in
    /// <see cref="RecycleBinEmptyResult.Failures"/> rather than emptied as that whole volume.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait. An empty already handed to the Shell always finishes.
    /// </param>
    /// <returns>What was removed, and what could not be reached.</returns>
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

        var targets = ResolveVolumes(volumeRoot);
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
            .RunAsync(() => Empty(targets, volumeRoot), cancellationToken)
            .ConfigureAwait(false);

        return emptied with { Elapsed = stopwatch.Elapsed };
    }

    /// <summary>
    /// Reports whether the Recycle Bin is in use for a volume, and why.
    /// </summary>
    /// <param name="volumeRoot">Volume root to inspect, such as <c>"E:\\"</c>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The status of that volume.</returns>
    /// <remarks>
    /// This answers whether the Recycle Bin is configured for the volume, not whether the next delete
    /// will land there. A volume mounted after this call, or a policy applied after it, changes the
    /// answer without this call being wrong. It needs no COM, so it does not queue behind the Shell
    /// worker and never blocks behind an in-flight batch.
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

    private static RecycleBinContents Collect()
    {
        var items = new List<RecycleBinItem>();
        var failures = new List<RecycleFailure>();

        RecycleBinLister.TryEnumerate(items, failures);

        // The elapsed time is measured by the caller, around the wait for this worker, so that it covers
        // what the caller waited rather than how long this part of it took.
        return new RecycleBinContents(items, failures, TimeSpan.Zero);
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
    private static RecycleBinEmptyResult Empty(IReadOnlyList<string> targets, string? volumeRoot)
    {
        var failures = new List<RecycleFailure>();
        var emptied = new List<string>(targets.Count);

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

        var hrEmpty = PInvoke.SHEmptyRecycleBin(default, volumeRoot, EmptyFlags);

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
    /// Chooses the volumes an operation covers: exactly the one asked for, or every mounted volume
    /// that can hold a Recycle Bin.
    /// </summary>
    /// <returns>
    /// The volume roots to act on. <see langword="null"/> when the caller named a path that is not a
    /// volume root, which is answered as not found rather than quietly widened to the volume containing it.
    /// </returns>
    /// <remarks>
    /// Network drives have no Recycle Bin, so measuring them would report a problem the caller cannot
    /// act on.
    /// <para>
    /// A path inside a volume is refused rather than resolved to that volume. Emptying is not a read:
    /// asking about <c>E:\some\folder</c> and clearing every recycled item on <c>E:\</c> is a much larger
    /// action than the one that was asked for, and a caller who mistyped a root deserves to be told the
    /// path was not one rather than to find out from what disappeared. <see cref="GetStatusAsync"/>
    /// answers the same input the same way.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string>? ResolveVolumes(string? volumeRoot)
    {
        if (volumeRoot is not null)
        {
            return IsVolumeRoot(volumeRoot) ? [NormalizeRoot(volumeRoot)] : null;
        }

        var drives = DriveInfo.GetDrives();
        var roots = new List<string>(drives.Length);

        foreach (var drive in drives)
        {
            if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
            {
                roots.Add(NormalizeRoot(drive.Name));
            }
        }

        return roots;
    }

    /// <summary>
    /// Whether the path is a volume root rather than something inside one — the trailing separator, or
    /// its absence for a bare drive letter, is what separates them and what callers get wrong.
    /// </summary>
    private static bool IsVolumeRoot(string path) =>
        string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);

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

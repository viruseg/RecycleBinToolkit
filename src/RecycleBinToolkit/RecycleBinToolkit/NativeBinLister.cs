using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Reads a volume's Recycle Bin by enumerating its entries.
/// </summary>
/// <remarks>
/// What an item is — a directory or a file, and how large it is — comes from the bin directory itself, which
/// is what makes a listing of eighteen thousand entries cost one walk rather than eighteen thousand reads.
/// The two fields that are only in the record are not read at all unless someone asks for them, or unless
/// <see cref="RecycleBinOptions.PreloadItemDetails"/> asks for all of them up front.
/// <para>
/// Volumes are read one after another rather than together. Two volumes can sit on different hardware, so a
/// single degree of parallelism would be wrong for one of them, and a share has no seek penalty to ask about
/// and a latency that multiplying would only make worse.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class NativeBinLister
{
    /// <summary>
    /// Reads every volume's Recycle Bin and reports what could be read.
    /// </summary>
    /// <param name="volumeRoots">Volume roots to read.</param>
    /// <param name="options">
    /// Settings already normalised. <see cref="RecycleBinOptions.PreloadItemDetails"/> is the only one that
    /// reaches this code.
    /// </param>
    /// <param name="cancellationToken">Cancels the enumeration and the reading.</param>
    /// <returns>
    /// The entries that could be described, plus one failure per record that could not be read. The elapsed
    /// time is zero because the caller measures the wait instead.
    /// </returns>
    internal static RecycleBinContents TryEnumerate(
        IReadOnlyList<string> volumeRoots,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);

        var items = new List<RecycleBinItem>();
        var failures = new List<RecycleFailure>();

        foreach (var root in volumeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateVolume(root, options, items, failures, cancellationToken);
        }

        return new RecycleBinContents(items, failures, TimeSpan.Zero);
    }

    private static void EnumerateVolume(
        string volumeRoot,
        RecycleBinOptions options,
        List<RecycleBinItem> items,
        List<RecycleFailure> failures,
        CancellationToken cancellationToken)
    {
        var binDirectory = NativeBinLayout.BinDirectory(volumeRoot);

        // A volume nothing has ever been recycled on has no directory here at all, which is an empty bin and
        // not a problem. Returning before enumerating is what keeps a fresh volume from being reported as
        // unreadable.
        if (!Directory.Exists(binDirectory))
        {
            return;
        }

        var entries = NativeBinEntries.Collect(binDirectory, cancellationToken);
        var found = Describe(entries, volumeRoot, options, failures, cancellationToken);

        foreach (var item in found)
        {
            if (item is not null)
            {
                items.Add(item);
            }
        }
    }

    /// <summary>
    /// Turns what the walk found into items, indexed by position.
    /// </summary>
    /// <remarks>
    /// Only a whole pair becomes an item. A payload nothing describes has no original path to answer with, and
    /// a record whose payload is gone is what the next empty is for rather than an entry a caller can be told
    /// about — reporting it would mean a listing whose every row raises when a caller reads its path.
    /// </remarks>
    private static RecycleBinItem?[] Describe(
        IReadOnlyList<NativeBinEntry> entries,
        string volumeRoot,
        RecycleBinOptions options,
        List<RecycleFailure> failures,
        CancellationToken cancellationToken)
    {
        var pairs = new List<NativeBinEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (entry.IndexPath is not null && entry.PayloadPath is not null)
            {
                pairs.Add(entry);
            }
        }

        if (pairs.Count == 0)
        {
            return [];
        }

        if (!options.PreloadItemDetails)
        {
            // Nothing is read here. Every item answers its own first access to its details, and a listing
            // nobody reads a path from costs nothing for having not read one.
            var described = new RecycleBinItem?[pairs.Count];

            for (var index = 0; index < pairs.Count; index++)
            {
                var pair = pairs[index];
                described[index] = new RecycleBinItem(pair.IndexPath!, pair.PayloadPath!, pair.PayloadLength, pair.PayloadIsDirectory);
            }

            return described;
        }

        return Preload(pairs, volumeRoot, failures, cancellationToken);
    }

    /// <summary>
    /// Reads every record up front, in parallel, and answers each item's details from what it says.
    /// </summary>
    /// <remarks>
    /// The reading is the parallel part and it is the whole cost of the mode, so it gets the volume's own
    /// degree of parallelism — the same measure every other read of a volume here uses, and the reason one
    /// volume's disk is not read as hard as another's.
    /// <para>
    /// Indexed by position rather than appended to a shared collection: a collection it had to lock would put
    /// the lock on the path the work is meant to speed up. Failures are the rare half — one record somebody
    /// else is holding — and are the only thing gathered under a lock.
    /// </para>
    /// <para>
    /// A record that reads but is not one is dropped without a failure, exactly as it was before the details
    /// became lazy: it says nothing about the bin, so reporting it as an entry a caller could not read would
    /// be noise about a file that is not part of it.
    /// </para>
    /// </remarks>
    private static RecycleBinItem?[] Preload(
        List<NativeBinEntry> pairs,
        string volumeRoot,
        List<RecycleFailure> failures,
        CancellationToken cancellationToken)
    {
        var warmed = new RecycleBinItem?[pairs.Count];
        var unreadable = new List<RecycleFailure>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = VolumeSeekPenalty.DegreeOfParallelism(volumeRoot),
            CancellationToken = cancellationToken
        };

        Parallel.For(0, pairs.Count, options, index =>
        {
            var pair = pairs[index];

            var item = RecycleBinItem.Warmed(
                pair.IndexPath!,
                pair.PayloadPath!,
                pair.PayloadLength,
                pair.PayloadIsDirectory,
                out var hResult);

            if (item is not null)
            {
                warmed[index] = item;
            }
            else if (hResult != 0)
            {
                lock (unreadable)
                {
                    unreadable.Add(new RecycleFailure(pair.IndexPath!, hResult, 1, RecycleFailureStage.Enumeration));
                }
            }
        });

        failures.AddRange(unreadable);
        return warmed;
    }
}

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
/// Volumes are read together, each on its own thread. A Recycle Bin is one directory per volume, so two
/// volumes are two directories on two devices and nothing about them contends: one disk waiting on its head
/// does not hold up another. The per-volume degree of parallelism
/// (<see cref="VolumeSeekPenalty.DegreeOfParallelism"/>) still decides how many threads each volume's own
/// records are read on, and that is the figure that has to answer to the drive — it says nothing about how
/// many volumes there are.
/// </para>
/// <para>
/// The answer's <em>order</em> does not follow the threads. Each volume writes into a slot taken by its
/// position in the list that was handed in, and the slots are flattened in that order once every walk is
/// done, so two listings of the same machine are comparable and a caller can match a group to the volume it
/// asked about.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
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
    /// One group per volume asked about, in the order they were asked about, plus one failure per record
    /// that could not be read. The elapsed time is zero because the caller measures the wait instead.
    /// </returns>
    internal static RecycleBinContents TryEnumerate(
        IReadOnlyList<string> volumeRoots,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);
        ArgumentNullException.ThrowIfNull(options);

        var groups = new VolumeGroup[volumeRoots.Count];

        cancellationToken.ThrowIfCancellationRequested();

        // One volume has nothing to be parallel with. The caller has already asked for a thread of its own
        // to run this on, and Parallel.For over a single index would nest another one inside it for no work.
        if (volumeRoots.Count == 1)
        {
            groups[0] = ReadVolume(volumeRoots[0], options, cancellationToken);
        }
        else
        {
            Parallel.For(
                0,
                volumeRoots.Count,
                new ParallelOptions
                {
                    // A walk is a chain of directory queries against one device, so a volume past the first
                    // adds nothing but a wait. The degree that matters is how many volumes there are.
                    MaxDegreeOfParallelism = volumeRoots.Count,
                    CancellationToken = cancellationToken
                },
                index => groups[index] = ReadVolume(volumeRoots[index], options, cancellationToken));
        }

        var failures = new List<RecycleFailure<ListFailureStage>>();
        var items = new List<RecycleBinVolumeContents>(groups.Length);

        foreach (var group in groups)
        {
            items.Add(new RecycleBinVolumeContents(group.VolumeRoot, group.Items));
            failures.AddRange(group.Failures);
        }

        return new RecycleBinContents(items, failures, TimeSpan.Zero);
    }

    /// <summary>
    /// One volume's walk and everything read out of it, held aside until every other volume's is done.
    /// </summary>
    /// <param name="VolumeRoot">Volume root this was read from.</param>
    /// <param name="Items">The entries that volume's bin holds, empty if it holds none.</param>
    /// <param name="Failures">Records on this volume that could not be read.</param>
    private sealed record VolumeGroup(string VolumeRoot, List<RecycleBinItem> Items, List<RecycleFailure<ListFailureStage>> Failures);

    private static VolumeGroup ReadVolume(
        string volumeRoot,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        var items = new List<RecycleBinItem>();
        var failures = new List<RecycleFailure<ListFailureStage>>();

        EnumerateVolume(volumeRoot, options, items, failures, cancellationToken);

        return new VolumeGroup(volumeRoot, items, failures);
    }

    private static void EnumerateVolume(
        string volumeRoot,
        RecycleBinOptions options,
        List<RecycleBinItem> items,
        List<RecycleFailure<ListFailureStage>> failures,
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
        List<RecycleFailure<ListFailureStage>> failures,
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
        List<RecycleFailure<ListFailureStage>> failures,
        CancellationToken cancellationToken)
    {
        var warmed = new RecycleBinItem?[pairs.Count];
        var unreadable = new List<RecycleFailure<ListFailureStage>>();

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
                    unreadable.Add(new RecycleFailure<ListFailureStage>(pair.IndexPath!, hResult, 1, ListFailureStage.Enumeration));
                }
            }
        });

        failures.AddRange(unreadable);
        return warmed;
    }
}

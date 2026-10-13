using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Empties a volume's Recycle Bin by deleting the entries in it, and reports what that removed.
/// </summary>
/// <remarks>
/// The counts come from the walk itself: it says what each entry is and how large its payload is, so an
/// empty of ninety thousand entries measures them without reading a record.
/// <para>
/// Volumes are split by the physical disk behind them and emptied disk by disk: volumes on different
/// devices run together, because a Recycle Bin is one directory per volume and nothing about one device
/// holds up another. The volumes of one disk run one after another, because they are two directories
/// sharing one head.
/// </para>
/// <para>
/// The reported volumes and failures follow the order the roots were passed in, not the order the threads
/// finished: each volume writes a slot taken by its position in the argument.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class NativeBinEmptier
{
    /// <summary>
    /// Removes everything from the named volumes and reports what went.
    /// </summary>
    /// <param name="volumeRoots">Volume roots to empty, each normalised with a trailing separator.</param>
    /// <param name="cancellationToken">
    /// Cancels the sweep between entries: the entry it is in the middle of is finished and the next is
    /// not begun.
    /// </param>
    /// <returns>
    /// Every volume whose bin directory existed, the exact number of entries removed, their exact total
    /// size, and one failure per entry that would not go.
    /// </returns>
    internal static RecycleBinEmptyResult Empty(
        IReadOnlyList<string> volumeRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);

        // One slot per root, in the caller's order. A slot still carrying its opening values is a volume
        // nothing has been decided for; a volume enters the work once and leaves it once, so no two
        // threads ever write the same one.
        var outcomes = new VolumeOutcome[volumeRoots.Count];

        var disks = VolumeDiskGroups.Group(volumeRoots);

        // One disk has nothing to be parallel with: the caller has already asked for a thread of its own to
        // run this on, and a Parallel.For over a single index would nest another one inside it for no work.
        if (disks.Count == 1)
        {
            RunDisk(disks[0], volumeRoots, outcomes, cancellationToken);
        }
        else if (disks.Count > 1)
        {
            Parallel.For(
                0,
                disks.Count,
                new ParallelOptions
                {
                    // Each disk is a chain of walks and deletions against one device, so a disk past the
                    // first adds nothing but a wait. The degree that matters is how many there are.
                    MaxDegreeOfParallelism = disks.Count,
                    CancellationToken = cancellationToken
                },
                index => RunDisk(disks[index], volumeRoots, outcomes, cancellationToken));
        }

        return Flatten(volumeRoots, outcomes);
    }

    /// <summary>
    /// Empties one disk's volumes, and returns before the next one starts.
    /// </summary>
    /// <remarks>
    /// A loop rather than a number, and deliberately: these volumes are partitions of one device, so they
    /// are two directories sharing one head, and no figure inside here could widen the gap. Which volumes
    /// land together is <see cref="VolumeDiskGroups"/>'s decision; running them one after another is this
    /// one's.
    /// </remarks>
    private static void RunDisk(
        VolumeDiskGroup disk,
        IReadOnlyList<string> volumeRoots,
        VolumeOutcome[] outcomes,
        CancellationToken cancellationToken)
    {
        foreach (var position in disk.Positions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            outcomes[position] = EmptyVolume(volumeRoots[position], cancellationToken);
        }
    }

    /// <summary>
    /// Flattens the per-volume slots into one answer, in the order the roots were passed.
    /// </summary>
    /// <remarks>
    /// Reading the slots rather than whatever the threads appended is the whole of why the answer does not
    /// follow the work: two volumes emptied at once report in the order the caller named them, whichever
    /// finished first. Nothing here locks, because every slot was written once before this runs.
    /// </remarks>
    private static RecycleBinEmptyResult Flatten(IReadOnlyList<string> volumeRoots, VolumeOutcome[] outcomes)
    {
        var failures = new List<RecycleFailure>();
        var emptied = new List<string>(outcomes.Length);
        long itemsRemoved = 0;
        long bytesFreed = 0;

        for (var position = 0; position < outcomes.Length; position++)
        {
            var outcome = outcomes[position];

            itemsRemoved += outcome.Removed;
            bytesFreed += outcome.Freed;

            if (outcome.Emptied)
            {
                emptied.Add(volumeRoots[position]);
            }

            failures.AddRange(outcome.Failures);
        }

        return new RecycleBinEmptyResult(emptied, itemsRemoved, bytesFreed, failures, TimeSpan.Zero);
    }

    private static VolumeOutcome EmptyVolume(
        string volumeRoot,
        CancellationToken cancellationToken)
    {
        var binDirectory = NativeBinLayout.BinDirectory(volumeRoot);

        // Two different situations hide behind one missing directory, and reporting them the same way would
        // swallow one of them: a volume nothing has ever been recycled on, and a volume that is not there
        // at all. The first is an empty bin, and an empty bin is emptied; the second is a caller who asked
        // to empty something that does not exist, and silence about that reads as "it was empty".
        if (!Directory.Exists(binDirectory))
        {
            if (!Directory.Exists(volumeRoot))
            {
                return new VolumeOutcome(
                    false,
                    0,
                    0,
                    [new RecycleFailure(volumeRoot, RecycleBinVolumeState.UnreadableVolume, 1, RecycleFailureStage.Enumeration)]);
            }

            return new VolumeOutcome(true, 0, 0, []);
        }

        var records = CollectRecords(binDirectory, cancellationToken);

        if (records.Count == 0)
        {
            return new VolumeOutcome(true, 0, 0, []);
        }

        // Measured before anything is deleted, out of bytes that are about to stop existing. A whole pair
        // already carries its size from the walk, so only the entries whose payload is gone have anything
        // left to ask for — and a record is the only thing that can answer.
        var sizes = new long[records.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = VolumeSeekPenalty.DegreeOfParallelism(volumeRoot),
            CancellationToken = cancellationToken
        };

        var needy = new List<int>();
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].PayloadPath is not null)
            {
                sizes[i] = records[i].PayloadIsDirectory ? 0 : records[i].PayloadLength;
            }
            else
            {
                sizes[i] = -1;
                needy.Add(i);
            }
        }

        // The token rides on the measuring pass as well as the sweep. A cancelled empty that still opened
        // and read every entry before noticing would take as long as the empty it was meant to abandon.
        Parallel.ForEach(needy, options, index => sizes[index] = SizeOfRecord(records[index].IndexPath!));

        var failures = new List<RecycleFailure>();
        Remove(records, sizes, failures, cancellationToken);

        // A volume is named as emptied only when nothing on it was left behind: a caller reading
        // VolumesEmptied is asking whether that volume is empty now, and a bin that kept a locked entry
        // is not. The failures themselves name the entries, so nothing is lost by leaving the volume off
        // the list.
        long removed = 0;
        long freed = 0;

        foreach (var size in sizes)
        {
            if (size >= 0)
            {
                removed++;
                freed += size;
            }
        }

        return new VolumeOutcome(failures.Count == 0, removed, freed, failures);
    }

    /// <summary>
    /// How much an entry whose payload is gone holds, or <c>-1</c> when it could not be worked out and its
    /// size is therefore not counted.
    /// </summary>
    /// <remarks>
    /// A record that cannot be read is still an entry to delete, and deleting it is what was asked for. Only
    /// what it holds is unknown, so only the figure is given up — the removal still happens and the entry
    /// still leaves the bin.
    /// </remarks>
    private static long SizeOfRecord(string indexPath) =>
        NativeIndexFile.TryRead(indexPath, out var record, out _) ? record.Size : -1;

    /// <summary>
    /// Deletes every entry, payload first and record second.
    /// </summary>
    /// <remarks>
    /// The order is the whole design, and it is the reverse of what looks safer. Deleting the record first
    /// and crashing leaves a payload that no record describes: invisible to the Shell, unattributable, and
    /// occupying disk until somebody finds it by hand. Deleting the payload first and crashing leaves a
    /// record whose payload has gone, which the next empty — this one included — collects without being told
    /// which volume it was on.
    /// </remarks>
    private static void Remove(
        List<NativeBinEntry> records,
        long[] sizes,
        List<RecycleFailure> failures,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexPath = records[i].IndexPath!;
            var payloadPath = records[i].PayloadPath;

            try
            {
                // A record with no payload is the only entry here with nothing to delete first, and it is
                // deleted the same way — the walk already established that there is no payload to delete.
                if (payloadPath is not null)
                {
                    DeleteEntry(payloadPath);
                }

                File.Delete(indexPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Somebody else emptied the same bin between the enumeration and here. The payload is gone
                // and the record that named it is the only thing left, so it goes on its own: the entry is
                // out of the bin either way, which is exactly what was asked for, and calling it a failure
                // would report a removal as a problem for every entry a concurrent empty had already taken.
                TryDeleteIndex(indexPath);
                sizes[i] = -1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Somebody is holding an entry open, or this account may not remove it. Neither stops the
                // rest of the sweep: an entry left behind costs the caller one line in the list, not the
                // whole empty.
                failures.Add(new RecycleFailure(indexPath, ex.HResult, 1, RecycleFailureStage.Enumeration));
                sizes[i] = -1;
            }
        }
    }

    /// <summary>
    /// Removes an entry's payload, whichever kind of thing it is.
    /// </summary>
    private static void DeleteEntry(string payloadPath)
    {
        if (Directory.Exists(payloadPath))
        {
            Directory.Delete(payloadPath, recursive: true);
            return;
        }

        File.Delete(payloadPath);
    }

    /// <summary>
    /// Removes the record of an entry whose payload had already gone.
    /// </summary>
    private static void TryDeleteIndex(string indexPath)
    {
        try
        {
            File.Delete(indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A record nothing will ever restore, left where the next empty on this volume finds it.
        }
    }

    /// <summary>
    /// Every entry a bin directory holds that has a record to delete, whether its payload is still there or
    /// not.
    /// </summary>
    /// <param name="binDirectory">The volume's bin directory for this user.</param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <remarks>
    /// An entry with a record and no payload is included, because a record with nothing behind it is litter
    /// that this is what removes. One with neither is not an entry at all and is not here to be swept.
    /// </remarks>
    private static List<NativeBinEntry> CollectRecords(string binDirectory, CancellationToken cancellationToken)
    {
        var records = new List<NativeBinEntry>();

        foreach (var entry in NativeBinEntries.Collect(binDirectory, cancellationToken))
        {
            if (entry.IndexPath is not null)
            {
                records.Add(entry);
            }
        }

        return records;
    }
}

/// <summary>
/// What one volume's bin contributed, held aside until every other volume's is done.
/// </summary>
/// <param name="Emptied">
/// Whether nothing was left behind on that volume. False when an entry would not delete, which is what
/// keeps a volume off the caller's <see cref="RecycleBinEmptyResult.VolumesEmptied"/>.
/// </param>
/// <param name="Removed">Entries that left the Recycle Bin on this volume.</param>
/// <param name="Freed">The bytes those entries occupied.</param>
/// <param name="Failures">
/// Entries on this volume that would not go, and volumes that could not be reached at all.
/// </param>
/// <remarks>
/// One volume's answer rather than appends to lists shared by the call, and that is what makes the disks
/// safe to run together: a shared list would interleave two volumes' failures and leave no way to say which
/// volume kept an entry. The values are summed and the lists concatenated only once every disk is done, and
/// in the caller's order.
/// </remarks>
internal readonly record struct VolumeOutcome(
    bool Emptied,
    long Removed,
    long Freed,
    List<RecycleFailure> Failures);
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Empties a volume's Recycle Bin by deleting the entries in it, and reports what that removed.
/// </summary>
/// <remarks>
/// The counts come from the walk itself: it says what each entry is and how large its payload is, so an
/// empty of ninety thousand entries measures them without reading a record. <c>SHEmptyRecycleBin</c> reports
/// whether it worked and says nothing about what it removed, which is why the Shell path samples
/// <c>SHQueryRecycleBin</c> on both sides of the call and reports the difference — a number that a recycle
/// happening at the same moment can move.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class NativeBinEmptier
{
    /// <summary>
    /// Removes everything from the named volumes and reports what went.
    /// </summary>
    /// <param name="volumeRoots">Volume roots to empty, each normalised with a trailing separator.</param>
    /// <param name="cancellationToken">
    /// Cancels the sweep between entries. Unlike the Shell path, which cannot stop an empty it has started,
    /// this one stops — the entry it is in the middle of is finished and the next is not begun.
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

        var failures = new List<RecycleFailure>();
        var emptied = new List<string>(volumeRoots.Count);
        long itemsRemoved = 0;
        long bytesFreed = 0;

        foreach (var root in volumeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (removed, freed) = EmptyVolume(root, emptied, failures, cancellationToken);
            itemsRemoved += removed;
            bytesFreed += freed;
        }

        return new RecycleBinEmptyResult(emptied, itemsRemoved, bytesFreed, failures, TimeSpan.Zero);
    }

    private static (long Removed, long Freed) EmptyVolume(
        string volumeRoot,
        List<string> emptied,
        List<RecycleFailure> failures,
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
                failures.Add(new RecycleFailure(volumeRoot, RecycleBinCounter.UnreadableVolume, 1, RecycleFailureStage.Enumeration));
                return (0, 0);
            }

            emptied.Add(volumeRoot);
            return (0, 0);
        }

        var failuresBefore = failures.Count;
        var records = CollectRecords(binDirectory, cancellationToken);

        if (records.Count == 0)
        {
            emptied.Add(volumeRoot);
            return (0, 0);
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

        Remove(records, sizes, failures, cancellationToken);

        // A volume is named as emptied only when nothing on it was left behind, which is the rule the Shell
        // path applies for the same reason: a caller reading VolumesEmptied is asking whether that volume
        // is empty now, and a bin that kept a locked entry is not. The failures themselves name the entries,
        // so nothing is lost by leaving the volume off the list.
        if (failures.Count == failuresBefore)
        {
            emptied.Add(volumeRoot);
        }

        var removed = 0L;
        var freed = 0L;

        foreach (var size in sizes)
        {
            if (size >= 0)
            {
                removed++;
                freed += size;
            }
        }

        return (removed, freed);
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
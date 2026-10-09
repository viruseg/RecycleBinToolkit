using System.IO.Enumeration;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Empties a volume's Recycle Bin by deleting the entries in it, and reports what that removed.
/// </summary>
/// <remarks>
/// The counts come from the records themselves. <c>SHEmptyRecycleBin</c> reports whether it worked and
/// says nothing about what it removed, which is why the Shell path samples <c>SHQueryRecycleBin</c> on
/// both sides of the call and reports the difference — a number that a recycle happening at the same moment
/// can move. Here every entry carries its own size, so the totals are read rather than inferred.
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
        // at all. The first is an empty bin; the second is a caller who asked to empty something that does
        // not exist, and silence about that reads as "it was empty".
        if (!Directory.Exists(binDirectory))
        {
            if (!Directory.Exists(volumeRoot))
            {
                failures.Add(new RecycleFailure(volumeRoot, RecycleBinCounter.UnreadableVolume, 1, RecycleFailureStage.Enumeration));
            }

            return (0, 0);
        }

        var records = CollectRecordPaths(binDirectory);

        if (records.Length == 0)
        {
            emptied.Add(volumeRoot);
            return (0, 0);
        }

        // Measured before anything is deleted, out of bytes that are about to stop existing. A directory's
        // own size is not counted, which is what Windows itself records for one and what the counter the
        // caller compares against also leaves out.
        var sizes = new long[records.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = VolumeSeekPenalty.DegreeOfParallelism(volumeRoot) };

        Parallel.For(0, records.Length, options, index =>
            sizes[index] = SizeOf(records[index]));

        Remove(records, sizes, failures, cancellationToken);

        emptied.Add(volumeRoot);

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
    /// How much an entry holds, or <c>-1</c> when it could not be worked out and its size is therefore not
    /// counted.
    /// </summary>
    /// <remarks>
    /// A record that cannot be read is still an entry to delete, and deleting it is what was asked for. It
    /// is measured from the payload on disk instead, because reporting a count that is short by an unknown
    /// amount would be worse than spending one more metadata call to get it right. Only a payload that cannot
    /// be measured either leaves the entry out of the totals, and nothing is reported for it because
    /// nothing failed — the removal still happens.
    /// </remarks>
    private static long SizeOf(string indexPath)
    {
        if (NativeIndexFile.TryRead(indexPath, out var record, out _))
        {
            return record.Size;
        }

        var payloadPath = NativeBinLayout.ToPayloadPath(indexPath);

        try
        {
            if (Directory.Exists(payloadPath))
            {
                return 0;
            }

            var size = new FileInfo(payloadPath).Length;
            return size < 0 ? -1 : size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

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
        string[] records,
        long[] sizes,
        List<RecycleFailure> failures,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < records.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexPath = records[i];
            var payloadPath = NativeBinLayout.ToPayloadPath(indexPath);

            try
            {
                DeleteEntry(payloadPath);
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
    /// The index records in a bin directory. <see cref="EnumerationOptions.AttributesToSkip"/> is zero
    /// because both halves of an entry carry <see cref="FileAttributes.Hidden"/> and
    /// <see cref="FileAttributes.System"/>, and the default would enumerate nothing at all.
    /// </summary>
    private static string[] CollectRecordPaths(string binDirectory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = true
        };

        var enumerable = new FileSystemEnumerable<string>(
            binDirectory,
            (ref FileSystemEntry entry) => Path.Combine(binDirectory, entry.FileName.ToString()),
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                !entry.IsDirectory && entry.FileName.StartsWith("$I", StringComparison.OrdinalIgnoreCase)
        };

        return [.. enumerable];
    }
}
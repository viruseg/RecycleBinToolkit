using System.IO.Enumeration;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Reads a volume's Recycle Bin by enumerating its index records, with no Shell anywhere in the path.
/// </summary>
/// <remarks>
/// Two passes per volume. The first enumerates the directory and collects the paths of the index records;
/// the second reads them, in parallel, into an array indexed by position. Splitting them is what lets the
/// reading run on several threads while the result still comes back in the order the directory gave it —
/// a concurrent collection would have cost either an ordering pass or a lock per entry.
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
    /// <param name="cancellationToken">Cancels the enumeration and the reading.</param>
    /// <returns>
    /// The entries that could be described, plus one failure per record that could not be read, parsed, or
    /// stat-ed. The elapsed time is zero because the caller measures the wait instead.
    /// </returns>
    internal static RecycleBinContents TryEnumerate(
        IReadOnlyList<string> volumeRoots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volumeRoots);

        var items = new List<RecycleBinItem>();
        var failures = new List<RecycleFailure>();

        foreach (var root in volumeRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnumerateVolume(root, items, failures, cancellationToken);
        }

        return new RecycleBinContents(items, failures, TimeSpan.Zero);
    }

    private static void EnumerateVolume(
        string volumeRoot,
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

        var records = CollectRecordPaths(binDirectory, cancellationToken);

        if (records.Length == 0)
        {
            return;
        }

        var found = new RecycleBinItem?[records.Length];
        var unreadable = new List<RecycleFailure>();

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = VolumeSeekPenalty.DegreeOfParallelism(volumeRoot),
            CancellationToken = cancellationToken
        };

        // Indexed by position rather than appended to a shared collection: the reading is the parallel part,
        // and a collection it had to lock would put the lock on the path the work is meant to speed up.
        // Failures are the rare half — one entry somebody else is holding — and are the only thing gathered
        // under a lock.
        Parallel.For(0, records.Length, options, index =>
        {
            if (NativeIndexFile.TryRead(records[index], out var record, out var hResult))
            {
                found[index] = Describe(record, records[index]);
            }
            else if (hResult != 0)
            {
                lock (unreadable)
                {
                    unreadable.Add(new RecycleFailure(records[index], hResult, 1, RecycleFailureStage.Enumeration));
                }
            }
        });

        foreach (var item in found)
        {
            if (item is not null)
            {
                items.Add(item);
            }
        }

        failures.AddRange(unreadable);
    }

    /// <summary>
    /// The paths of every index record in a bin directory.
    /// </summary>
    /// <remarks>
    /// The filter is applied while the directory is being enumerated rather than afterwards, so a payload
    /// and a <c>desktop.ini</c> never cross into user mode at all. It cannot be left to the pattern alone
    /// either: .NET's default <see cref="EnumerationOptions.AttributesToSkip"/> is
    /// <see cref="FileAttributes.Hidden"/> and <see cref="FileAttributes.System"/>, and both entries carry
    /// both attributes — left at the default, this enumerates an empty directory.
    /// </remarks>
    private static string[] CollectRecordPaths(string binDirectory, CancellationToken cancellationToken)
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

        var records = new List<string>();

        foreach (var path in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(path);
        }

        return [.. records];
    }

    /// <summary>
    /// Turns a record into what a caller sees, asking the filesystem the one thing the record cannot answer.
    /// </summary>
    /// <remarks>
    /// A version 2 record stores zero for a directory and for an empty file alike, so
    /// <see cref="RecycleBinItem.IsDirectory"/> cannot come from it — one metadata operation on the payload
    /// is what tells them apart.
    /// <para>
    /// A payload that is not there is not a failure. A record whose payload was removed between the
    /// enumeration and this stat is an entry the bin still knows about, and reporting it is more use to a
    /// caller than dropping it because one file has gone missing.
    /// </para>
    /// </remarks>
    private static RecycleBinItem Describe(NativeIndexRecord record, string indexPath)
    {
        var payloadPath = NativeBinLayout.ToPayloadPath(indexPath);

        bool isDirectory;
        try
        {
            isDirectory = (File.GetAttributes(payloadPath) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The record says what was deleted; only whether it was a directory is missing, and a file is the
            // answer that is true for an entry whose payload has since been purged.
            isDirectory = false;
        }

        return new RecycleBinItem(
            Path.GetFileName(record.OriginalPath),
            record.OriginalPath,
            payloadPath,
            isDirectory ? 0 : record.Size,
            new DateTimeOffset(record.DeletedAtUtc.ToLocalTime()),
            isDirectory);
    }
}
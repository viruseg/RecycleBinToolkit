using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the records a listing left unread, in as much parallel as each volume's own hardware allows.
/// </summary>
/// <remarks>
/// Volumes are read one after another rather than together. Two volumes can sit on different hardware, so a
/// single degree of parallelism would be wrong for one of them, and a share has no seek penalty to ask about
/// and a latency that multiplying would only make worse — the same reasoning the listing and the empty already
/// follow.
/// <para>
/// <b>One loop, and the thread count is the only thing the drive type decides.</b> Splitting the reads from the
/// parsing across a channel was tried and measured, on both volumes, with both a
/// <c>System.Threading.Channels</c> channel and a <see cref="System.Collections.Concurrent.BlockingCollection{T}"/>
/// pipeline over dedicated threads. Every version of it lost to the plain loop: on a mechanical volume 395 ms
/// against 351 ms with a channel and 545 ms against 496 ms with the pipeline in the library itself, and on a
/// solid-state one 426 ms against 420 ms. The reason is that there is nothing to overlap — a read is three
/// system calls and about 47 microseconds, and parsing one costs about a tenth of a microsecond, so the parse
/// is two parts in a thousand of the work. Splitting it buys a queue and a second set of threads for a
/// rounding error.
/// </para>
/// <para>
/// Overlapped reads were tried for the same reason and are worse still: 599 ms against 351 ms on a mechanical
/// volume and 563 ms against 467 ms on a solid-state one. A 92-byte read that the cache already holds is
/// dominated by the cost of the open, and an overlapped open has to hand the work to the completion port and
/// wait to be told it finished.
/// </para>
/// <para>
/// A mechanical volume used to get one thread here, on the reasoning that a head asked for four hundred files
/// at once thrashes between them. That is true of <em>writing</em> a bin, where every record lands in one
/// directory, and it is not true of reading one: thirty thousand records are thirty thousand independent opens
/// of files nobody is writing to. Removing that rule is where most of the speed came from — measured against
/// the code as it stood, 50.7 s to 7.9 s cold and 1.650 s to 0.491 s warm, over 30,010 records.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class ItemDetailLoader
{
    /// <summary>
    /// Reads every record in <paramref name="items"/> that has not been read yet.
    /// </summary>
    /// <param name="items">The entries of one listing.</param>
    /// <param name="cancellationToken">Cancels the work between records, and between volumes.</param>
    /// <remarks>
    /// An entry that cannot be read is left cold rather than reported: the method has no failure channel of its
    /// own, and the entry's own documented behaviour — raising when a caller asks — is already the honest answer
    /// for a record that is not there.
    /// </remarks>
    internal static void Load(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var group in GroupByVolume(items, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadVolume(group.Key, group.Value, cancellationToken);
        }
    }

    /// <summary>
    /// The entries still waiting for a record, gathered under the root of the volume each one lives on.
    /// </summary>
    /// <remarks>
    /// Keyed without regard to case, as the root itself is: Windows writes <c>E:\</c> and <c>e:\</c> for the
    /// same volume and would otherwise have the same disk read twice under two degrees of parallelism.
    /// <para>
    /// One pass, and a second entry on the same volume costs a span comparison rather than a dictionary probe.
    /// A listing that named one volume — which is every listing a caller took one volume of — has all of its
    /// entries under the same root, so the ordinary case is a string comparison per entry and no hashing at
    /// all. Only an entry that actually moves to another volume pays for the dictionary, and the lookup is
    /// re-taken from there on rather than rebuilt per entry.
    /// </para>
    /// </remarks>
    private static Dictionary<string, List<RecycleBinItem>> GroupByVolume(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<RecycleBinItem>>(StringComparer.OrdinalIgnoreCase);
        var lookup = groups.GetAlternateLookup<ReadOnlySpan<char>>();

        List<RecycleBinItem>? lastGroup = null;
        ReadOnlySpan<char> lastRoot = default;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];

            if (item.IsDetailsLoaded)
            {
                continue;
            }

            // Asked once and kept: the root is a span over the entry's own path, and asking for it is a
            // call into the path parser. NeedsDetails asks for it as well, so this is where the second one
            // would have been spent.
            var root = item.VolumeRoot;

            if (root.IsEmpty)
            {
                continue;
            }

            if (lastGroup is null || !root.Equals(lastRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!lookup.TryGetValue(root, out lastGroup))
                {
                    lastGroup = [];
                    groups.Add(root.ToString(), lastGroup);
                    lookup = groups.GetAlternateLookup<ReadOnlySpan<char>>();
                }

                lastRoot = root;
            }

            lastGroup!.Add(item);
        }

        return groups;
    }

    /// <summary>
    /// Reads one volume's records, on as many threads as that volume answers for.
    /// </summary>
    private static void LoadVolume(
        string volumeRoot,
        List<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var degree = VolumeSeekPenalty.DegreeOfParallelism(volumeRoot);

        if (degree <= 1)
        {
            // A share, and a volume nothing answered for: there is nothing to overlap and a second thread
            // would only add a network round trip's worth of latency to the first.
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items[i].LoadDetails(out _);
            }

            return;
        }

        // Index-addressed rather than a shared collection, because the reads are the parallel part and a
        // collection they had to lock would put the lock on the work the threading exists to speed up.
        //
        // The token is also tested inside the body, not only through ParallelOptions: the options make the loop
        // stop between chunks, which is still a chunk of work. Testing it per record is what makes "cancels the
        // work between records" true rather than nearly true.
        Parallel.For(
            0,
            items.Count,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            index =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    items[index].LoadDetails(out _);
                }
            });
    }
}
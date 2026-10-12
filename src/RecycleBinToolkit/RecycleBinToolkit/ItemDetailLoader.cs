using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the records a listing left unread, in as much parallel as each volume's own hardware allows.
/// </summary>
/// <remarks>
/// Volumes are read one after another rather than together, and that is the opposite of what the listing
/// does. Two volumes can sit on different hardware, so a single degree of parallelism would be wrong for one
/// of them, and a share has no seek penalty to ask about and a latency that multiplying would only make
/// worse. The listing has no such question — it reads each volume's directory once, whatever the volume is —
/// which is why it can take them all at once and this cannot.
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
    /// Reads every record in <paramref name="items"/> that has not been read yet, telling
    /// <paramref name="onReady"/> about each one as it is read rather than after the last.
    /// </summary>
    /// <typeparam name="T">The signal's type. A <see langword="struct"/> keeps the call off the heap.</typeparam>
    /// <param name="items">The entries of one listing.</param>
    /// <param name="cancellationToken">Cancels the work between records, and between volumes.</param>
    /// <param name="onReady">
    /// Told about every entry this call reads, with the code that read reported. A caller that also
    /// has preloaded entries to report signals those itself, from its own pass over
    /// <paramref name="items"/> — <see cref="GroupByVolume"/> skips them, which is what saves a path
    /// parse per preloaded entry, and a caller wanting them grouped would put that cost back.
    /// </param>
    /// <remarks>
    /// The signal is inline and synchronous because a pipeline was measured against this loop and
    /// lost on both volume types: there is nothing to overlap, because a read is three system calls
    /// and about 47 microseconds while parsing one costs about a tenth of a microsecond. A queue
    /// between the read and the signal would cost more than the whole parse.
    /// </remarks>
    internal static void Load<T>(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken, in T onReady)
        where T : IRestoreSignal
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var group in GroupByVolume(items, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadVolume(group.Key, group.Value, cancellationToken, in onReady);
        }
    }

    /// <summary>
    /// The entries still waiting for a record, gathered under the root of the volume each one lives on.
    /// </summary>
    /// <remarks>
    /// Keyed without regard to case, as the root itself is: Windows writes <c>X:\</c> and <c>x:\</c> for the
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

    /// <summary>
    /// Reads one volume's records, on as many threads as that volume answers for, telling
    /// <paramref name="onReady"/> about each as it lands.
    /// </summary>
    /// <remarks>
    /// A second copy rather than a parameter on <see cref="LoadVolume(string, List{RecycleBinItem}, CancellationToken)"/>,
    /// because a parameter would put a test on the loop the measurements above were taken on: the
    /// only way to skip that test is for the compiler to know the signal is a no-op, which means the
    /// existing overload stops being the code those numbers describe.
    /// </remarks>
    private static void LoadVolume<T>(
        string volumeRoot,
        List<RecycleBinItem> items,
        CancellationToken cancellationToken,
        in T onReady)
        where T : IRestoreSignal
    {
        var degree = VolumeSeekPenalty.DegreeOfParallelism(volumeRoot);

        if (degree <= 1)
        {
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Signal(items[i], i, in onReady);
            }

            return;
        }

        // Copied out of the `in` parameter because a lambda may not capture one. A signal is a struct
        // that reaches shared state through a reference, so every copy names the same place.
        var signal = onReady;

        Parallel.For(
            0,
            items.Count,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            index =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Signal(items[index], index, in signal);
                }
            });
    }

    /// <summary>
    /// Reads one entry's record and tells the signal what the read reported.
    /// </summary>
    /// <remarks>
    /// The signal is called whether or not the record was readable, and a read that failed is one
    /// the caller needs to hear about as much as one that worked: an entry whose record cannot be
    /// read is still an entry to move. The failure code is the only thing distinguishing them.
    /// </remarks>
    private static void Signal<T>(RecycleBinItem item, int index, in T onReady)
        where T : IRestoreSignal
    {
        item.LoadDetails(out var hResult);
        onReady.OnReady(index, item, hResult);
    }
}

/// <summary>
/// Told, once per entry, that its record has been read and what that read reported.
/// </summary>
/// <remarks>
/// Called synchronously from the thread that read the record, so a signal that must not race itself
/// needs no locking of its own.
/// <para>
/// An entry the listing already read is not reported through here. It reaches the caller through
/// <see cref="RecycleBinItem.IsDetailsLoaded"/> instead, which costs one field read, because
/// <c>ItemDetailLoader</c> skips it before it knows which volume it lives on.
/// </para>
/// </remarks>
internal interface IRestoreSignal
{
    /// <summary>
    /// Reports one entry whose record has now been read.
    /// </summary>
    /// <param name="index">
    /// The entry's position among the entries read together on one volume. Entries arrive out of
    /// order when a volume is read on several threads, so this is what tells two of them apart.
    /// <para>
    /// It is the position in the list the caller handed in only when that list holds one volume
    /// and every entry before this one in it was unread. A caller that needs its own numbering
    /// back — one placing results into slots — should hand over one volume's entries at a time,
    /// which is also how it reads them.
    /// </para>
    /// </param>
    /// <param name="item">The entry, carrying whatever the read resolved.</param>
    /// <param name="hResult">
    /// What the read reported: <c>0</c> when the record was readable or turned out not to be a
    /// record, and the filesystem's own code when it could not be opened at all. An entry reported
    /// by a caller's own preloaded pass carries <c>0</c>, because there was no read to report.
    /// </param>
    void OnReady(int index, RecycleBinItem item, int hResult);
}

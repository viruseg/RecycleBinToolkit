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
/// A volume that answers "mechanical" gets one thread and a plain loop, because the alternative is not slower,
/// it is harmful: a head asked for four hundred files at once thrashes between them. A volume that answers
/// "solid-state" gets up to <see cref="VolumeSeekPenalty.ReadDegreeCap"/> threads, which is where the
/// measurements on this machine stopped improving.
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

        // Nothing to do is the common case twice over: a Shell listing, and a native listing that read its
        // records up front. Both are answered by a single walk of the items that touches no disk.
        if (!HasPending(items, cancellationToken))
        {
            return;
        }

        foreach (var group in GroupByVolume(items, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadVolume(group.Key, group.Value, cancellationToken);
        }
    }

    /// <summary>
    /// Whether any entry is still waiting for its record.
    /// </summary>
    /// <remarks>
    /// Asked before anything is allocated, because the answer is usually no and the allocation for a yes is a
    /// dictionary and a list per volume. <paramref name="cancellationToken"/> is checked here so that a cancelled
    /// call reports cancellation instead of quietly returning having done nothing.
    /// </remarks>
    private static bool HasPending(IReadOnlyList<RecycleBinItem> items, CancellationToken cancellationToken)
    {
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            if (!item.IsDetailsLoaded && item.VolumeRoot is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The entries still waiting for a record, gathered under the root of the volume each one lives on.
    /// </summary>
    /// <remarks>
    /// Keyed without regard to case, as the root itself is: Windows writes <c>E:\</c> and <c>e:\</c> for the
    /// same volume and would otherwise have the same disk read twice under two degrees of parallelism.
    /// </remarks>
    private static Dictionary<string, List<RecycleBinItem>> GroupByVolume(
        IReadOnlyList<RecycleBinItem> items,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<RecycleBinItem>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];

            if (item.IsDetailsLoaded || item.VolumeRoot is not { } root)
            {
                continue;
            }

            if (!groups.TryGetValue(root, out var group))
            {
                group = new List<RecycleBinItem>();
                groups.Add(root, group);
            }

            group.Add(item);
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
            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items[i].LoadDetails(out _);
            }

            return;
        }

        // Index-addressed rather than a shared collection, because the reads are the parallel part and a
        // collection they had to lock would put the lock on the work the threading exists to speed up.
        Parallel.For(
            0,
            items.Count,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            index => items[index].LoadDetails(out _));
    }
}

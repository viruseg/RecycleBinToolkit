namespace RecycleBinToolkit;

/// <summary>
/// The outcome of <see cref="RecycleBin.EmptyAsync"/>: what was emptied, what it cost, and what could
/// not be reached.
/// </summary>
public sealed record RecycleBinEmptyResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="volumesEmptied">Volume roots that were emptied.</param>
    /// <param name="itemsRemoved">Number of entries that left the Recycle Bin.</param>
    /// <param name="bytesFreed">Bytes those entries occupied.</param>
    /// <param name="failures">Volumes that could not be emptied or measured.</param>
    /// <param name="elapsed">
    /// Wall-clock time the caller waited, from handing the work over to getting this result back.
    /// </param>
    public RecycleBinEmptyResult(
        IReadOnlyList<string> volumesEmptied,
        long itemsRemoved,
        long bytesFreed,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed)
    {
        VolumesEmptied = volumesEmptied;
        ItemsRemoved = itemsRemoved;
        BytesFreed = bytesFreed;
        Failures = failures;
        Elapsed = elapsed;
    }

    /// <summary>
    /// Volume roots the operation covered, each with a trailing separator. Emptying every volume
    /// reports every mounted volume that can hold a Recycle Bin.
    /// </summary>
    /// <remarks>
    /// In the order the operation covered them. A volume is named only when nothing was left behind on
    /// it, so a caller reading this list is reading the volumes that are empty now.
    /// <para>
    /// The order is this one however the work was spread: volumes on different physical disks are emptied
    /// at the same time, and each writes its own answer rather than appending to a shared one, so the list
    /// does not follow whichever disk happened to finish first.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> VolumesEmptied { get; }

    /// <summary>
    /// Number of entries that left the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Measured, never taken from a success code: every entry's own record carries its size, so the
    /// totals are summed from the entries being deleted. Never negative.
    /// </remarks>
    public long ItemsRemoved { get; }

    /// <summary>
    /// Bytes those entries occupied, measured the same way as <see cref="ItemsRemoved"/>.
    /// </summary>
    public long BytesFreed { get; }

    /// <summary>
    /// What could not be emptied. Empty when everything asked for was emptied.
    /// </summary>
    /// <remarks>
    /// The volume when the volume itself could not be reached, and the individual index record when a
    /// single entry would not delete — because an entry is deleted one at a time and the caller is
    /// better served by knowing which one stayed.
    /// <para>
    /// In the order the operation covered the volumes, as <see cref="VolumesEmptied"/> is, and every
    /// failure of one volume together rather than interleaved with another volume's.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>
    /// Wall-clock time the caller waited, from handing the work over to getting this result back.
    /// </summary>
    public TimeSpan Elapsed { get; init; }
}

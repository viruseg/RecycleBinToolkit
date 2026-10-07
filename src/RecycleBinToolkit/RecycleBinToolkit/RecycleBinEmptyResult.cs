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
    /// Wall-clock time the caller waited. Set by <see cref="RecycleBin.EmptyAsync"/> around the wait for
    /// the Shell worker, since it is the only place that knows both.
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
    public IReadOnlyList<string> VolumesEmptied { get; }

    /// <summary>
    /// Number of entries that left the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Measured by querying each volume before and after, never derived from the Shell's success code:
    /// the Shell reports success whether it removed ten entries or none. Never negative — a bin that
    /// grew while the operation ran is reported as zero rather than as a negative removal.
    /// </remarks>
    public long ItemsRemoved { get; }

    /// <summary>
    /// Bytes those entries occupied, measured the same way as <see cref="ItemsRemoved"/>.
    /// </summary>
    public long BytesFreed { get; }

    /// <summary>
    /// Volumes that could not be emptied, or whose before-and-after count could not be read. Empty
    /// when everything asked for was emptied.
    /// </summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>
    /// Wall-clock time the caller waited, from handing the work over to getting this result back. That
    /// includes waiting for the Shell worker to be free, not just the empty itself.
    /// </summary>
    public TimeSpan Elapsed { get; init; }
}

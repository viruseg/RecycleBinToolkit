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
    /// <para>
    /// What a path in this list is depends on the backend. The Shell path names the volume that could not
    /// be emptied or could not be measured. The native path names the volume when the volume itself could not
    /// be reached, and the individual index record when a single entry would not delete — because on that
    /// path an entry is deleted one at a time and the caller is better served by knowing which one stayed.
    /// </para>
    /// </summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>
    /// Wall-clock time the caller waited, from handing the work over to getting this result back. On the
    /// Shell backend that includes waiting for the worker to be free, which for a library that queues
    /// every operation behind one thread is most of what a caller waits for; the native backend has no
    /// queue, and the same measurement is the work plus the hop onto the thread pool.
    /// </summary>
    public TimeSpan Elapsed { get; init; }
}

namespace RecycleBinToolkit;

/// <summary>The outcome of one <see cref="RecycleBin.EmptyAsync(string?, EmptyOptions?, CancellationToken)"/> call.</summary>
public sealed record RecycleBinEmptyResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="volumesEmptied">Volume roots that were emptied.</param>
    /// <param name="itemsRemoved">Number of entries that left the Recycle Bin.</param>
    /// <param name="bytesFreed">Bytes those entries occupied.</param>
    /// <param name="failures">Volumes that could not be emptied or measured.</param>
    /// <param name="elapsed">Time the call took.</param>
    public RecycleBinEmptyResult(
        IReadOnlyList<string> volumesEmptied,
        long itemsRemoved,
        long bytesFreed,
        IReadOnlyList<RecycleFailure<EmptyFailureStage>> failures,
        TimeSpan elapsed)
    {
        VolumesEmptied = volumesEmptied;
        ItemsRemoved = itemsRemoved;
        BytesFreed = bytesFreed;
        Failures = failures;
        Elapsed = elapsed;
    }

    /// <summary>Volume roots that were emptied, each with a trailing separator.</summary>
    /// <remarks>
    /// In the order the operation covered them, not the order the work finished. A volume is named only when
    /// nothing was left behind on it.
    /// </remarks>
    public IReadOnlyList<string> VolumesEmptied { get; }

    /// <summary>Number of entries that left the Recycle Bin.</summary>
    /// <remarks>
    /// An entry is a recycled item's two halves, or whichever half of them the bin still held: a record whose
    /// payload is gone and a payload whose record is gone both count.
    /// </remarks>
    public long ItemsRemoved { get; }

    /// <summary>Total bytes those entries occupied.</summary>
    /// <remarks>
    /// A directory contributes nothing to this figure: its size is not measured, only its entry is removed. An
    /// entry whose record could not be read is removed but not counted here.
    /// </remarks>
    public long BytesFreed { get; }

    /// <summary>Volumes that could not be emptied or measured.</summary>
    /// <remarks>
    /// In the order the operation covered the volumes, as <see cref="VolumesEmptied"/> is, with each volume's
    /// failures together.
    /// </remarks>
    public IReadOnlyList<RecycleFailure<EmptyFailureStage>> Failures { get; }

    /// <summary>Time the call took.</summary>
    public TimeSpan Elapsed { get; init; }
}

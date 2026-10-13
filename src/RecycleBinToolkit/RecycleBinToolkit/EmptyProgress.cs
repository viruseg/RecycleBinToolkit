namespace RecycleBinToolkit;

/// <summary>How one entry an empty removed ended.</summary>
public enum EmptyStatus
{
    /// <summary>The entry is out of the Recycle Bin.</summary>
    Removed = 0,

    /// <summary>The entry is still there. <see cref="RecycleBinEmptyResult.Failures"/> carries why.</summary>
    Failed = 1
}

/// <summary>What became of one entry an empty removed.</summary>
/// <remarks>
/// One per entry the operation went after. An empty reports entries leaving the Recycle Bin rather than
/// where they were, so there is no path here: the entries that did not go are named by
/// <see cref="RecycleBinEmptyResult.Failures"/>. <see cref="Count"/> may be more than the number of reports
/// that arrive, because the count is taken before the sweep and an entry added meanwhile is not part of it.
/// </remarks>
/// <param name="Index">Position of this entry in the operation's own numbering, starting at 1.</param>
/// <param name="Count">Entries the operation counted before it began.</param>
/// <param name="Status">Whether the entry is out of the bin.</param>
/// <param name="Elapsed">Time from the start of the operation to the moment this report was created.</param>
public sealed record EmptyProgress(
    int Index,
    int Count,
    EmptyStatus Status,
    TimeSpan Elapsed);
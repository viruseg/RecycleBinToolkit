namespace RecycleBinToolkit;

/// <summary>The outcome of one entry in an <see cref="RecycleBin.EmptyAsync(string?, EmptyOptions?, CancellationToken)"/> call.</summary>
public enum EmptyStatus
{
    /// <summary>The entry is out of the Recycle Bin.</summary>
    Removed = 0,

    /// <summary>The entry is still there. <see cref="RecycleBinEmptyResult.Failures"/> carries why.</summary>
    Failed = 1
}

/// <summary>
/// The report <see cref="RecycleBin.EmptyAsync(string?, EmptyOptions?, CancellationToken)"/> raises for one
/// entry, carrying its position in the count.
/// </summary>
/// <remarks>
/// One per entry the operation went after. An <see cref="RecycleBin.EmptyAsync(string?, EmptyOptions?, CancellationToken)"/>
/// call reports entries leaving the Recycle Bin rather than
/// where they were, so there is no path here: the entries that did not go are named by
/// <see cref="RecycleBinEmptyResult.Failures"/>, by their record inside the bin rather than by where they
/// were. <para>
/// <see cref="Count"/> is counted before the sweep, and the bin can change between the count and the
/// sweep: an entry added afterwards pushes <see cref="Index"/> past it, and one taken by somebody else is
/// counted but never swept. A caller dividing by <see cref="Count"/> should clamp.
/// </para>
/// </remarks>
/// <param name="Index">Position of this entry in the operation's own numbering, starting at 1.</param>
/// <param name="Count">Entries the operation counted before it began.</param>
/// <param name="Status">Whether the entry is out of the bin.</param>
/// <param name="Elapsed">Time from the start of the call to the moment this report was created.</param>
public sealed record EmptyProgress(
    int Index,
    int Count,
    EmptyStatus Status,
    TimeSpan Elapsed);
namespace RecycleBinToolkit;

/// <summary>
/// Tuning knobs for a Recycle Bin operation.
/// </summary>
/// <remarks>
/// Every property has a production-ready default, so <see cref="Default"/> is a valid argument for
/// every call. A single value is overridden with an object initialiser:
/// <c>new RecycleBinOptions { ThrowOnError = false }</c>. Out-of-range values are clamped rather than
/// rejected, so an unvalidated instance cannot produce a zero-attempt or negative-delay loop.
/// </remarks>
public sealed class RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static RecycleBinOptions Default { get; } = new();

    /// <summary>How many times an item is attempted before it is reported as a failure. Defaults to <c>6</c>, clamped to at least 1.</summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>
    /// Delay before the first retry, doubling on every subsequent attempt. Defaults to <c>50</c>
    /// milliseconds; negative values clamp to zero.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Upper bound on the wall-clock time spent retrying the whole batch. Defaults to <c>30</c> seconds.
    /// One budget for the call, not one per item or per volume: a backoff that does not fit what is left of
    /// it ends the loop immediately rather than starting an over-budget round.
    /// </summary>
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a failure raises an exception instead of being reported through
    /// <see cref="RecycleBinResult.Failures"/>. Defaults to <c>true</c>.
    /// </summary>
    public bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// What happens to a file whose volume cannot recycle it. Defaults to
    /// <see cref="NoRecycleBinAction.Throw"/>, which refuses it and leaves the file in place.
    /// <see cref="ThrowOnError"/> still decides whether the refusal raises or is returned through
    /// <see cref="RecycleBinResult.Failures"/>.
    /// </summary>
    public NoRecycleBinAction NoRecycleBinAction { get; init; } = NoRecycleBinAction.Throw;

    /// <summary>
    /// Whether a listing reads every entry's record up front instead of leaving each entry to read its own
    /// on first use. Defaults to <c>false</c>, so a listing pays nothing for details nobody asks for.
    /// </summary>
    /// <remarks>
    /// Turned on, the reads happen during the listing rather than one at a time from the caller's own loop,
    /// and an entry whose record cannot be read is reported in <see cref="RecycleBinContents.Failures"/>
    /// rather than being returned and raising later. A caller that reads every item's path pays the same
    /// either way, only spread differently.
    /// <para>
    /// It says nothing about <see cref="RecycleBin.EmptyAsync"/>, which does not read records for entries
    /// whose payload is present.
    /// </para>
    /// </remarks>
    public bool PreloadItemDetails { get; init; }

    /// <summary>
    /// What a restore does with a directory whose original path is already occupied. Defaults to
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/>, which merges and leaves what is already there as it
    /// is — a file of the tree that does not fit goes back into the Recycle Bin as an entry of its own
    /// rather than being lost with the payload it was in.
    /// </summary>
    /// <remarks>
    /// It says nothing about <see cref="ThrowOnError"/>: a restore is a batch, so a conflict is data —
    /// resolved ones in <see cref="RecycleBinRestoreResult.Conflicts"/>, and the ones
    /// <see cref="RestoreDirectoryConflict.Error"/> refuses in <see cref="RecycleBinRestoreResult.Failures"/>
    /// as well. No single item raises.
    /// <para>
    /// A merge reports a conflict per file it settled rather than per entry, and none of the three reaches a
    /// failure stage <em>of its own</em>: a file of the tree the filesystem would not take, and a payload or
    /// a record that would not go afterwards, are reported at
    /// <see cref="RecycleFailureStage.RestoreFailed"/> like any other refused move.
    /// </para>
    /// </remarks>
    public RestoreDirectoryConflict DirectoryConflict { get; init; } = RestoreDirectoryConflict.MergeSkip;

    /// <summary>
    /// What a restore does with a file whose original path is already occupied. Defaults to
    /// <see cref="RestoreFileConflict.Skip"/>, which leaves the file where the caller put it and leaves the
    /// entry in the Recycle Bin.
    /// </summary>
    public RestoreFileConflict FileConflict { get; init; } = RestoreFileConflict.Skip;

    /// <summary>
    /// Returns a copy with out-of-range values clamped into a usable range.
    /// </summary>
    // Every property is named here, because a property this initialiser does not mention is not clamped
    // but reset — silently, on every internal call, back to the property initialiser's default. A caller
    // who asked for one behaviour would get another with nothing raised and nothing reported.
    internal RecycleBinOptions Normalize() => new()
    {
        MaxAttempts = Math.Max(1, MaxAttempts),
        InitialRetryDelay = InitialRetryDelay < TimeSpan.Zero ? TimeSpan.Zero : InitialRetryDelay,
        OverallTimeout = ClampTimeout(OverallTimeout),
        ThrowOnError = ThrowOnError,
        NoRecycleBinAction = NoRecycleBinAction,
        PreloadItemDetails = PreloadItemDetails,
        DirectoryConflict = DirectoryConflict,
        FileConflict = FileConflict
    };

    /// <summary>
    /// Keeps the overall budget short enough that adding the time already spent cannot overflow.
    /// </summary>
    // The deadline is the elapsed time plus this budget, and a caller is free to set a budget close to
    // TimeSpan.MaxValue. That addition throws, and it throws from the retry loop of an operation documented
    // never to throw — before any retry decision has been made at all.
    private static TimeSpan ClampTimeout(TimeSpan overallTimeout)
    {
        if (overallTimeout < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var headroom = TimeSpan.MaxValue - MaxElapsedAllowance;
        return overallTimeout > headroom ? headroom : overallTimeout;
    }

    /// <summary>
    /// Longest run the elapsed time is allowed to reach, which bounds the addition above.
    /// </summary>
    private static readonly TimeSpan MaxElapsedAllowance = TimeSpan.FromDays(365);
}
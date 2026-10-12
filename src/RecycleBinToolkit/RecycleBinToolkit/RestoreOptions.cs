namespace RecycleBinToolkit;

/// <summary>
/// Settings for <see cref="RecycleBin.RestoreAsync(IReadOnlyList{RecycleBinItem}, RecycleBinOptions?, CancellationToken)"/>
/// and its overload over the volume groups of a listing.
/// </summary>
/// <remarks>
/// Every property has a production default, so <see cref="Default"/> is a valid argument for either
/// overload. <see langword="null"/> means the same thing.
/// <para>
/// There is no <c>ThrowOnError</c> here because a restore is a batch: a conflict is data — the resolved
/// ones in <see cref="RecycleBinRestoreResult.Conflicts"/> and the ones a policy refuses in
/// <see cref="RecycleBinRestoreResult.Failures"/> as well — and no single entry raises.
/// </para>
/// </remarks>
public sealed class RestoreOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static RestoreOptions Default { get; } = new();

    /// <summary>How many times an item is attempted before it is reported as a failure. Defaults to <c>6</c>, clamped to at least 1.</summary>
    public new int MaxAttempts
    {
        get => base.MaxAttempts;
        init => base.MaxAttempts = value;
    }

    /// <summary>
    /// Delay before the first retry, doubling on every subsequent attempt. Defaults to <c>50</c>
    /// milliseconds; negative values clamp to zero.
    /// </summary>
    public new TimeSpan InitialRetryDelay
    {
        get => base.InitialRetryDelay;
        init => base.InitialRetryDelay = value;
    }

    /// <summary>
    /// Upper bound on the wall-clock time spent retrying the whole batch. Defaults to <c>30</c> seconds.
    /// One budget for the call, not one per item or per volume: a backoff that does not fit what is left of
    /// it ends the loop immediately rather than starting an over-budget round.
    /// </summary>
    public new TimeSpan OverallTimeout
    {
        get => base.OverallTimeout;
        init => base.OverallTimeout = value;
    }

    /// <summary>
    /// What a restore does with a directory whose original path is already occupied. Defaults to
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/>, which merges and leaves what is already there as it
    /// is — a file of the tree that does not fit goes back into the Recycle Bin as an entry of its own
    /// rather than being lost with the payload it was in.
    /// </summary>
    /// <remarks>
    /// A merge reports a conflict per file it settled rather than per entry, and none of the three reaches a
    /// failure stage <em>of its own</em>: a file of the tree the filesystem would not take, and a payload or
    /// a record that would not go afterwards, are reported at
    /// <see cref="RecycleFailureStage.RestoreFailed"/> like any other refused move.
    /// </remarks>
    public new RestoreDirectoryConflict DirectoryConflict
    {
        get => base.DirectoryConflict;
        init => base.DirectoryConflict = value;
    }

    /// <summary>
    /// What a restore does with a file whose original path is already occupied. Defaults to
    /// <see cref="RestoreFileConflict.Skip"/>, which leaves the file where the caller put it and leaves the
    /// entry in the Recycle Bin.
    /// </summary>
    public new RestoreFileConflict FileConflict
    {
        get => base.FileConflict;
        init => base.FileConflict = value;
    }
}
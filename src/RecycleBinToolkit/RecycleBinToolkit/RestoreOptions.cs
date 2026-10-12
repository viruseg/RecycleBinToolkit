namespace RecycleBinToolkit;

/// <summary>Settings for either <c>RestoreAsync</c> overload.</summary>
/// <remarks>
/// <see langword="null"/> means <see cref="Default"/>.
/// </remarks>
public sealed class RestoreOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static RestoreOptions Default { get; } = new();

    /// <summary>Attempts per entry before it is reported as a failure. Defaults to <c>6</c>, clamped to at least 1.</summary>
    public new int MaxAttempts
    {
        get => base.MaxAttempts;
        init => base.MaxAttempts = value;
    }

    /// <summary>
    /// Delay before the first retry, doubling on every subsequent attempt. Defaults to <c>50</c> milliseconds;
    /// negative values clamp to zero.
    /// </summary>
    public new TimeSpan InitialRetryDelay
    {
        get => base.InitialRetryDelay;
        init => base.InitialRetryDelay = value;
    }

    /// <summary>
    /// Upper bound on the time spent retrying. Defaults to <c>30</c> seconds. One budget for the call, not
    /// one per entry or per volume.
    /// </summary>
    public new TimeSpan OverallTimeout
    {
        get => base.OverallTimeout;
        init => base.OverallTimeout = value;
    }

    /// <summary>
    /// What a restore does with a directory whose original path is occupied. Defaults to
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/>.
    /// </summary>
    /// <remarks>
    /// A merge reports one conflict per file it settled rather than one per entry, and reports a refused file
    /// at <see cref="RecycleFailureStage.RestoreFailed"/> like any other move that would not happen.
    /// </remarks>
    public new RestoreDirectoryConflict DirectoryConflict
    {
        get => base.DirectoryConflict;
        init => base.DirectoryConflict = value;
    }

    /// <summary>
    /// What a restore does with a file whose original path is occupied. Defaults to
    /// <see cref="RestoreFileConflict.Skip"/>, which leaves the file on disk and the entry in the Recycle Bin.
    /// </summary>
    public new RestoreFileConflict FileConflict
    {
        get => base.FileConflict;
        init => base.FileConflict = value;
    }
}
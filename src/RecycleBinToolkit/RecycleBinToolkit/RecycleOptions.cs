namespace RecycleBinToolkit;

/// <summary>Settings for either <c>RecycleAsync</c> overload.</summary>
/// <remarks>
/// <see langword="null"/> means <see cref="Default"/>.
/// </remarks>
public sealed class RecycleOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static RecycleOptions Default { get; } = new();

    /// <summary>Attempts per item before it is reported as a failure. Defaults to <c>6</c>, clamped to at least 1.</summary>
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
    /// one per item or per volume.
    /// </summary>
    public new TimeSpan OverallTimeout
    {
        get => base.OverallTimeout;
        init => base.OverallTimeout = value;
    }

    /// <summary>
    /// Whether a failure raises rather than returning <see langword="false"/>. Defaults to <c>true</c>. Read
    /// only by <see cref="RecycleBin.RecycleAsync(string?, RecycleOptions?, CancellationToken)"/>: the batch
    /// overload reports its per-item failures through <see cref="RecycleBinResult.Failures"/> however this is
    /// set.
    /// </summary>
    public new bool ThrowOnError
    {
        get => base.ThrowOnError;
        init => base.ThrowOnError = value;
    }

    /// <summary>
    /// What happens to a path on a volume that cannot recycle it. Defaults to
    /// <see cref="NoRecycleBinAction.Throw"/>, which refuses it and leaves the file untouched.
    /// </summary>
    public new NoRecycleBinAction NoRecycleBinAction
    {
        get => base.NoRecycleBinAction;
        init => base.NoRecycleBinAction = value;
    }
}
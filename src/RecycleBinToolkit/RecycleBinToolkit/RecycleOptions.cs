namespace RecycleBinToolkit;

/// <summary>
/// Settings for <see cref="RecycleBin.RecycleAsync(string?, RecycleBinOptions?, CancellationToken)"/> and
/// its batch overload.
/// </summary>
/// <remarks>
/// Every property has a production default, so <see cref="Default"/> is a valid argument for either
/// overload. <see langword="null"/> means the same thing.
/// </remarks>
public sealed class RecycleOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static RecycleOptions Default { get; } = new();

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
    /// Whether a failure raises an exception instead of being reported through
    /// <see cref="RecycleBinResult.Failures"/>. Defaults to <c>true</c>.
    /// </summary>
    public new bool ThrowOnError
    {
        get => base.ThrowOnError;
        init => base.ThrowOnError = value;
    }

    /// <summary>
    /// What happens to a file whose volume cannot recycle it. Defaults to
    /// <see cref="NoRecycleBinAction.Throw"/>, which refuses it and leaves the file in place.
    /// <see cref="ThrowOnError"/> still decides whether the refusal raises or is returned through
    /// <see cref="RecycleBinResult.Failures"/>.
    /// </summary>
    public new NoRecycleBinAction NoRecycleBinAction
    {
        get => base.NoRecycleBinAction;
        init => base.NoRecycleBinAction = value;
    }
}
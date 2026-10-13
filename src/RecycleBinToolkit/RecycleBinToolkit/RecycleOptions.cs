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
    /// Whether a failure raises rather than returning <see langword="false"/>. Defaults to <c>true</c>.
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

    /// <summary>
    /// What happens to a path the Recycle Bin's index record cannot name. Defaults to
    /// <see cref="LongPathFailureAction.Throw"/>, which refuses it and leaves the file untouched.
    /// </summary>
    /// <remarks>
    /// Set <see cref="LongPathFailureAction.DeletePermanently"/> to remove it instead, in which case the
    /// path also appears in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// <para>
    /// A path only arrives here where the record in use has a fixed-size path field, so on a machine whose
    /// Recycle Bin can name any path Windows can, this setting changes nothing.
    /// </para>
    /// </remarks>
    public new LongPathFailureAction LongPathFailureAction
    {
        get => base.LongPathFailureAction;
        init => base.LongPathFailureAction = value;
    }

    /// <summary>
    /// Called once per path handed to the operation, on a thread of its own, so the operation does not wait
    /// for it. Defaults to <see langword="null"/>, which reports nothing and costs nothing.
    /// </summary>
    /// <remarks>
    /// Every path in the batch is reported, including one that was refused, repeated, or named nothing.
    /// Reports may still arrive after the call returns unless <see cref="WaitForProgressCallbacks"/> is set.
    /// </remarks>
    public new Action<RecycleProgress>? OnItemProcessed
    {
        get => (base.OnItemProcessed as RecycleProgressSink)?.Handler;
        init => base.OnItemProcessed = value is null ? null : new RecycleProgressSink(value);
    }

    /// <summary>
    /// Called once with whatever <see cref="OnItemProcessed"/> threw, after which that handler is not
    /// called again. Defaults to <see langword="null"/>, which drops it.
    /// </summary>
    public new Action<Exception>? OnProgressFailed
    {
        get => base.OnProgressFailed;
        init => base.OnProgressFailed = value;
    }

    /// <summary>
    /// Whether the call returns only once every report has been delivered. Defaults to
    /// <see langword="false"/>, so the operation is never held up by the handler.
    /// </summary>
    public new bool WaitForProgressCallbacks
    {
        get => base.WaitForProgressCallbacks;
        init => base.WaitForProgressCallbacks = value;
    }
}
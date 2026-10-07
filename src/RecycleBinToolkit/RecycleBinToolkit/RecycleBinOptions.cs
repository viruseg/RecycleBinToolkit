namespace RecycleBinToolkit;

/// <summary>
/// Tuning knobs for a Recycle Bin operation.
/// </summary>
/// <remarks>
/// Every property has a production-ready default, so <see cref="Default"/> is a valid argument for
/// every call. Use <c>with</c> initialiser syntax to override a single value.
/// </remarks>
public sealed class RecycleBinOptions
{
    /// <summary>
    /// Shared instance carrying the documented defaults.
    /// </summary>
    public static RecycleBinOptions Default { get; } = new();

    /// <summary>
    /// How many times an item is attempted before it is reported as a failure.
    /// </summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>
    /// Delay before the first retry. It doubles on every subsequent attempt.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Upper bound on the wall-clock time spent retrying the whole batch.
    /// </summary>
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When set, a reported post-delete location is rejected unless it is inside
    /// <c>$RECYCLE.BIN</c> on the item's volume.
    /// </summary>
    /// <remarks>
    /// This rejects a delete the Shell itself placed elsewhere. It cannot prove a file reached the
    /// Recycle Bin in the other direction: the Shell reports no location at all for some successful
    /// deletes, and a missing location cannot be distinguished from a delete the Shell completed
    /// without recycling. Turning this off trades that check for slightly less work per item.
    /// </remarks>
    public bool VerifyRecycleBinReceipt { get; init; } = true;

    /// <summary>
    /// Whether a failure raises an exception instead of being reported through
    /// <see cref="RecycleBinResult.Failures"/>.
    /// </summary>
    public bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// What happens to a file whose path is too long for the Shell to recycle.
    /// </summary>
    /// <remarks>
    /// The default refuses it and leaves the file in place. Set
    /// <see cref="LongPathFailureAction.DeletePermanently"/> to remove it instead, in which case the
    /// path also appears in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// </remarks>
    public LongPathFailureAction LongPathFailureAction { get; init; } = LongPathFailureAction.Throw;

    /// <summary>
    /// Longest path the Shell can be asked to recycle, in characters. Measured on this machine: a
    /// 251-character path recycles, a 261-character path is removed permanently while the operation
    /// still reports success.
    /// </summary>
    public int MaxShellPathLength { get; init; } = 260;

    /// <summary>
    /// Returns a copy with out-of-range values clamped into a usable range, so an unvalidated
    /// caller-supplied instance cannot produce a zero-attempt or negative-delay loop.
    /// </summary>
    internal RecycleBinOptions Normalize() => new()
    {
        MaxAttempts = Math.Max(1, MaxAttempts),
        InitialRetryDelay = InitialRetryDelay < TimeSpan.Zero ? TimeSpan.Zero : InitialRetryDelay,
        OverallTimeout = OverallTimeout < TimeSpan.Zero ? TimeSpan.Zero : OverallTimeout,
        VerifyRecycleBinReceipt = VerifyRecycleBinReceipt,
        ThrowOnError = ThrowOnError,
        LongPathFailureAction = LongPathFailureAction,
        MaxShellPathLength = Math.Max(1, MaxShellPathLength)
    };
}
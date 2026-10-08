namespace RecycleBinToolkit;

/// <summary>
/// Tuning knobs for a Recycle Bin operation.
/// </summary>
/// <remarks>
/// Every property has a production-ready default, so <see cref="Default"/> is a valid argument for
/// every call. A single value is overridden with an object initialiser:
/// <c>new RecycleBinOptions { ThrowOnError = false }</c>.
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
    /// <remarks>
    /// The default is <c>6</c>.
    /// </remarks>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>
    /// Delay before the first retry. It doubles on every subsequent attempt.
    /// </summary>
    /// <remarks>
    /// The default is <c>50</c> milliseconds.
    /// </remarks>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Upper bound on the wall-clock time spent retrying the whole batch.
    /// </summary>
    /// <remarks>
    /// The default is <c>30</c> seconds.
    /// </remarks>
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When set, a reported post-delete location is rejected unless it is inside
    /// <c>$RECYCLE.BIN</c> on the item's volume. The default is <c>true</c>.
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
    /// <remarks>
    /// The default is <c>true</c>.
    /// </remarks>
    public bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// What happens to a file whose path is too long for the Shell to recycle. The default is
    /// <see cref="LongPathFailureAction.Throw"/>, which refuses it and leaves the file in place.
    /// </summary>
    /// <remarks>
    /// Set <see cref="LongPathFailureAction.DeletePermanently"/> to remove it instead, in which case the
    /// path also appears in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// <para>
    /// None of this applies to a path on a volume that cannot recycle it. That volume is settled before
    /// the path's length is looked at, so such a path is refused at
    /// <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/> with the file left in place whichever value
    /// is set here; see <see cref="NoRecycleBinAction"/>.
    /// </para>
    /// </remarks>
    public LongPathFailureAction LongPathFailureAction { get; init; } = LongPathFailureAction.Throw;

    /// <summary>
    /// What happens to a file whose volume cannot recycle it. The default is
    /// <see cref="NoRecycleBinAction.Throw"/>, which refuses it and leaves the file in place.
    /// </summary>
    /// <remarks>
    /// This decides whether the path is handed to the Shell at all.
    /// <see cref="ThrowOnError"/> still decides whether the refusal raises or is returned through
    /// <see cref="RecycleBinResult.Failures"/>.
    /// </remarks>
    public NoRecycleBinAction NoRecycleBinAction { get; init; } = NoRecycleBinAction.Throw;

    /// <summary>
    /// Longest path the Shell can be asked to recycle, in characters. Measured on this machine: a
    /// 251-character path recycles, a 261-character path is removed permanently while the operation
    /// still reports success.
    /// </summary>
    /// <remarks>
    /// The default is <c>260</c>.
    /// </remarks>
    public int MaxShellPathLength { get; init; } = 260;

    /// <summary>
    /// Returns a copy with out-of-range values clamped into a usable range, so an unvalidated
    /// caller-supplied instance cannot produce a zero-attempt or negative-delay loop.
    /// </summary>
    internal RecycleBinOptions Normalize() => new()
    {
        MaxAttempts = Math.Max(1, MaxAttempts),
        InitialRetryDelay = InitialRetryDelay < TimeSpan.Zero ? TimeSpan.Zero : InitialRetryDelay,
        OverallTimeout = ClampTimeout(OverallTimeout),
        VerifyRecycleBinReceipt = VerifyRecycleBinReceipt,
        ThrowOnError = ThrowOnError,
        LongPathFailureAction = LongPathFailureAction,
        NoRecycleBinAction = NoRecycleBinAction,
        MaxShellPathLength = Math.Max(1, MaxShellPathLength)
    };

    /// <summary>
    /// Keeps the overall budget short enough that adding the time already spent cannot overflow.
    /// </summary>
    /// <remarks>
    /// The deadline is the elapsed time plus this budget, and a caller is free to set a budget close to
    /// <see cref="TimeSpan.MaxValue"/>. That addition throws, and it throws from the retry loop of an
    /// operation documented never to throw — before any retry decision has been made at all.
    /// </remarks>
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
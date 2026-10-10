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
    /// This rejects a delete placed outside the bin. It cannot prove a file reached the
    /// Recycle Bin in the other direction: no location is reported at all for some successful
    /// deletes, and a missing location cannot be distinguished from a completed delete.
    /// Turning this off trades that check for slightly less work per item.
    /// <para>
    /// The engine chooses the destination itself, so there is no reported location to disagree with
    /// anything, and the same question it would ask is true of every path by construction — it is
    /// asserted in a Debug build instead of run per item.
    /// </para>
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
    /// None of this applies to a path on a volume that cannot recycle it under the two actions that
    /// settle the volume first. <see cref="NoRecycleBinAction.Throw"/> refuses such a path at
    /// <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/> with the file left in place, and
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> has the volume gate remove it outright, so it
    /// arrives in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> from there rather than from
    /// here. Under <see cref="NoRecycleBinAction.AllowIndeterminate"/> the path is handed over, and
    /// this action is what then decides its fate. See <see cref="NoRecycleBinAction"/>.
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
    /// Whether a listing reads every Recycle Bin record up front instead of reading one when a caller
    /// first asks for its details. The default is <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="RecycleBinItem.OriginalPath"/> and <see cref="RecycleBinItem.DeletedAt"/> come from an
    /// entry's <c>$I</c> record, and a bin of eighteen thousand entries is eighteen thousand reads — about
    /// five seconds warm and thirty cold on a mechanical volume. Left off, a listing pays nothing for
    /// details nobody asks for. Turned on, they are read during the listing, in parallel, and an entry
    /// whose record cannot be read is reported in <see cref="RecycleBinContents.Failures"/> instead of
    /// being returned.
    /// <para>
    /// A caller that reads every item's path — to display them, to match them against its own paths —
    /// pays the same cost either way, spread over the listing rather than over its own loop.
    /// </para>
    /// <para>
    /// It says nothing about <see cref="RecycleBin.EmptyAsync"/>, which does not
    /// read records for entries whose payload is present.
    /// </para>
    /// </remarks>
    public bool PreloadItemDetails { get; init; }

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
        MaxShellPathLength = Math.Max(1, MaxShellPathLength),
        PreloadItemDetails = PreloadItemDetails
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
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
    /// Whether a failure raises an exception instead of being reported through
    /// <see cref="RecycleBinResult.Failures"/>.
    /// </summary>
    /// <remarks>
    /// The default is <c>true</c>.
    /// </remarks>
    public bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// What happens to a file whose volume cannot recycle it. The default is
    /// <see cref="NoRecycleBinAction.Throw"/>, which refuses it and leaves the file in place.
    /// </summary>
    /// <remarks>
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
    /// What a restore does with a directory whose original path is already occupied. The default is
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/>, which merges and leaves what is already there
    /// as it is.
    /// </summary>
    /// <remarks>
    /// It says nothing about <see cref="ThrowOnError"/>: a restore is a batch, so a conflict is data —
    /// resolved ones in <see cref="RecycleBinRestoreResult.Conflicts"/>, and the ones
    /// <see cref="RestoreDirectoryConflict.Error"/> refuses in <see cref="RecycleBinRestoreResult.Failures"/>
    /// as well. No single item raises.
    /// </remarks>
    public RestoreDirectoryConflict DirectoryConflict { get; init; } = RestoreDirectoryConflict.MergeSkip;

    /// <summary>
    /// What a restore does with a file whose original path is already occupied. The default is
    /// <see cref="RestoreFileConflict.Skip"/>, which leaves the file where the caller put it and leaves
    /// the item in the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Two properties rather than one shared policy, because a directory can be merged into what is
    /// already there and a file cannot; see <see cref="RestoreDirectoryConflict"/> for the other half.
    /// </remarks>
    public RestoreFileConflict FileConflict { get; init; } = RestoreFileConflict.Skip;

    /// <summary>
    /// Returns a copy with out-of-range values clamped into a usable range, so an unvalidated
    /// caller-supplied instance cannot produce a zero-attempt or negative-delay loop.
    /// </summary>
    /// <remarks>
    /// Every property is named here, because a property this initialiser does not mention is not clamped
    /// but reset — silently, on every internal call, back to the property initialiser's default. A caller
    /// who asked for one behaviour would get another with nothing raised and nothing reported.
    /// </remarks>
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
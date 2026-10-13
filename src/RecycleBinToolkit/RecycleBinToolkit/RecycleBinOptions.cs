namespace RecycleBinToolkit;

/// <summary>
/// The storage behind the settings one operation takes, and the base of the three types a caller can name.
/// </summary>
/// <remarks>
/// Neither constructible nor derivable outside this library, so the properties live here and each operation's
/// type exposes only the ones that operation reads: <see cref="RecycleOptions"/>,
/// <see cref="RestoreOptions"/> and <see cref="GetItemsOptions"/>. Out-of-range values are clamped rather than
/// rejected, so an unvalidated instance cannot produce a zero-attempt or negative-delay loop.
/// <para>
/// A property's contract is on the derived type that exposes it, which is the member a caller hovers; what
/// is left here is the storage and a line naming the engine that reads it.
/// </para>
/// </remarks>
public abstract class RecycleBinOptions
{
    internal RecycleBinOptions() { }

    /// <summary>
    /// Attempts allowed for one item. Read by <see cref="NativeRecycleRunner"/> and <see cref="RestoreRunner"/>,
    /// which are the two retry loops.
    /// </summary>
    internal int MaxAttempts { get; init; } = 6;

    /// <summary>
    /// Backoff before the first retry, doubled by each round's reader: <see cref="NativeRecycleRunner"/> and
    /// <see cref="RestoreRunner"/>.
    /// </summary>
    internal TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Budget for one whole call. Read once at the top of <see cref="NativeRecycleRunner"/> or
    /// <see cref="RestoreRunner"/> to build a deadline, and not again per item.
    /// </summary>
    internal TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a failure raises. Read only by
    /// <see cref="RecycleBin.RecycleAsync(string?, RecycleOptions?, CancellationToken)"/>, and only on the
    /// single-path overload: the batch reports its per-item failures as data however this is set.
    /// </summary>
    internal bool ThrowOnError { get; init; } = true;

    /// <summary>
    /// What to do with a path on a volume that cannot recycle it. Read by <see cref="RecycleBatch"/>, and
    /// nowhere else.
    /// </summary>
    internal NoRecycleBinAction NoRecycleBinAction { get; init; } = NoRecycleBinAction.Throw;

    /// <summary>
    /// What to do with a path the index record this machine's Shell reads cannot name. Defaults to
    /// <see cref="LongPathFailureAction.Throw"/>, which refuses it and leaves the file in place.
    /// </summary>
    /// <remarks>
    /// Reachable only where the record in use has a fixed path field, which on Windows is the older of the
    /// two formats. Read by <see cref="NativeRecycleRunner"/>, and nowhere else.
    /// </remarks>
    internal LongPathFailureAction LongPathFailureAction { get; init; } = LongPathFailureAction.Throw;

    /// <summary>
    /// Whether a listing reads every entry's record during the walk rather than leaving each entry to read
    /// its own on first use. Read by <see cref="NativeBinLister"/>.
    /// </summary>
    internal bool PreloadItemDetails { get; init; }

    /// <summary>
    /// What to do with a directory whose original path is already occupied. Read by
    /// <see cref="RestorePathPlanner"/> to answer that question, and by <see cref="RestoreRunner"/> when the
    /// answer decides whether it refuses, whether it merges, and what a merge does.
    /// </summary>
    internal RestoreDirectoryConflict DirectoryConflict { get; init; } = RestoreDirectoryConflict.MergeSkip;

    /// <summary>
    /// What to do with a file whose original path is already occupied. Read by
    /// <see cref="RestorePathPlanner"/> and <see cref="RestoreRunner"/>, as <see cref="DirectoryConflict"/> is.
    /// </summary>
    internal RestoreFileConflict FileConflict { get; init; } = RestoreFileConflict.Skip;

    /// <summary>
    /// The queue behind a caller's progress handler. Read by nothing itself: each operation carries its own
    /// queue down to its engine, because a settings object can reach two calls that overlap and one queue
    /// between them would let the first call's completion close the second's.
    /// </summary>
    internal ProgressSink? OnItemProcessed { get; init; }

    /// <summary>
    /// What to do about a progress handler that threw. Read by the three operations that deliver
    /// reports, and nowhere else.
    /// </summary>
    internal Action<Exception>? OnProgressFailed { get; init; }

    /// <summary>
    /// Whether the three operations drain the queue before returning. Read by the same three, and by
    /// nothing inside them.
    /// </summary>
    internal bool WaitForProgressCallbacks { get; init; }

    /// <summary>
    /// The queue this call is to write to, always a new one, or <see langword="null"/> when the caller
    /// asked for no reports.
    /// </summary>
    /// <remarks>
    /// Always new rather than the one the caller's initialiser built. The same settings object reaches two
    /// calls whenever a caller keeps one in a field, and those calls may overlap: the first one's
    /// completion would close the second one's queue, and every report the second made after that would be
    /// dropped with nothing raised and nothing reported.
    /// </remarks>
    internal ProgressSink? ArmProgress()
    {
        if (OnItemProcessed is null)
        {
            return null;
        }

        var armed = OnItemProcessed.Restart();
        armed.Fault = OnProgressFailed;
        return armed;
    }

    /// <summary>
    /// Queues one report about a recycled path, or does nothing at all when nobody asked for reports.
    /// </summary>
    internal static void Report(ProgressSink? sink, RecycleStatus status, string path, RecycleFailure? failure)
    {
        if (sink is null)
        {
            return;
        }

        sink.Report(new RecycleProgress(sink.NextIndex(), sink.Count, status, path, sink.Elapsed, failure));
    }

    /// <summary>
    /// Returns a copy with out-of-range values clamped into a usable range.
    /// </summary>
    // Every property is named here, because a property this initialiser does not mention is not clamped
    // but reset — silently, on every internal call, back to the property initialiser's default. A caller
    // who asked for one behaviour would get another with nothing raised and nothing reported.
    internal RecycleBinOptions Normalize() => new Normalized
    {
        MaxAttempts = Math.Max(1, MaxAttempts),
        InitialRetryDelay = InitialRetryDelay < TimeSpan.Zero ? TimeSpan.Zero : InitialRetryDelay,
        OverallTimeout = ClampTimeout(OverallTimeout),
        ThrowOnError = ThrowOnError,
        NoRecycleBinAction = NoRecycleBinAction,
        LongPathFailureAction = LongPathFailureAction,
        PreloadItemDetails = PreloadItemDetails,
        DirectoryConflict = DirectoryConflict,
        FileConflict = FileConflict,
        OnItemProcessed = OnItemProcessed,
        OnProgressFailed = OnProgressFailed,
        WaitForProgressCallbacks = WaitForProgressCallbacks
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

    /// <summary>
    /// A copy that keeps the base type, which is all the engine ever asks for. Declared here so the
    /// eight properties can be named in one initialiser; see the comment on <see cref="Normalize"/>.
    /// </summary>
    private sealed class Normalized : RecycleBinOptions
    {
    }
}
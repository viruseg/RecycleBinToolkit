namespace RecycleBinToolkit;

/// <summary>The outcome of one batch <see cref="RecycleBin.RecycleAsync(IReadOnlyList{string?}, RecycleOptions?, CancellationToken)"/> call.</summary>
public sealed class RecycleBinResult
{
    private RecycleBinResult(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<string> permanentlyDeletedPaths,
        IReadOnlyList<RecycleFailure<RecyclePathFailureStage>> failures,
        TimeSpan elapsed,
        IReadOnlyList<string> leftoverStagingDirectories)
    {
        RecycledPaths = recycledPaths;
        PermanentlyDeletedPaths = permanentlyDeletedPaths;
        Failures = failures;
        LeftoverStagingDirectories = leftoverStagingDirectories;
        Elapsed = elapsed;
    }

    /// <summary>
    /// <see langword="true"/> when every requested path is gone from disk: recycled, or deleted permanently
    /// because the caller asked for that, and nothing was stranded.
    /// </summary>
    public bool Succeeded => Failures.Count == 0 && LeftoverStagingDirectories.Count == 0;

    /// <summary>Paths, as supplied, that were recycled.</summary>
    public IReadOnlyList<string> RecycledPaths { get; }

    /// <summary>
    /// Paths, as supplied, that were deleted permanently instead of recycled because their volume cannot
    /// recycle them and <see cref="NoRecycleBinAction.DeletePermanently"/> was set. Empty unless that was
    /// taken, and a path here is never also in <see cref="RecycledPaths"/>.
    /// </summary>
    public IReadOnlyList<string> PermanentlyDeletedPaths { get; }

    /// <summary>
    /// Paths that were not handled. Empty when <see cref="Succeeded"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<RecycleFailure<RecyclePathFailureStage>> Failures { get; }

    /// <summary>
    /// Staging directories this operation created and could not remove because something was still inside
    /// them. Empty unless that happened.
    /// </summary>
    /// <remarks>
    /// A path here holds the caller's own data, on their disk, in a directory this library made. It is
    /// part of the outcome rather than a log line because a result reporting a clean success while their
    /// file sits in a directory they did not create is the outcome this library exists to prevent — and
    /// <see cref="Succeeded"/> counts it as a failure for the same reason.
    /// </remarks>
    public IReadOnlyList<string> LeftoverStagingDirectories { get; }

    /// <summary>Time the operation took, including retries.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The first recorded failure, or <see langword="null"/> when there is none.</summary>
    public RecycleFailure<RecyclePathFailureStage>? FirstFailure => Failures.Count > 0 ? Failures[0] : null;

    /// <summary>Creates a result.</summary>
    /// <param name="recycledPaths">Paths confirmed recycled, as supplied.</param>
    /// <param name="permanentlyDeletedPaths">Paths deleted permanently instead of recycled.</param>
    /// <param name="failures">Paths that were not handled.</param>
    /// <param name="elapsed">Time the operation took.</param>
    /// <param name="leftoverStagingDirectories">
    /// Staging directories left holding something. <see langword="null"/> means there were none.
    /// </param>
    public static RecycleBinResult Create(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<string> permanentlyDeletedPaths,
        IReadOnlyList<RecycleFailure<RecyclePathFailureStage>> failures,
        TimeSpan elapsed,
        IReadOnlyList<string>? leftoverStagingDirectories = null) =>
        new(recycledPaths, permanentlyDeletedPaths, failures, elapsed, leftoverStagingDirectories ?? []);

    /// <summary>
    /// Returns a copy whose failures are exactly <paramref name="failures"/>, keeping every other part of
    /// the outcome as it was.
    /// </summary>
    /// <remarks>
    /// A batch that rejects some of its paths before the move comes back needing the two
    /// failure lists joined. Rebuilding it through the constructor that has no
    /// <see cref="PermanentlyDeletedPaths"/> would quietly reset it, and that is the field a caller
    /// needs to learn that a file was deleted permanently.
    /// </remarks>
    /// <param name="failures">The failures the copy should carry, in the order they should be read.</param>
    internal RecycleBinResult WithFailures(IReadOnlyList<RecycleFailure<RecyclePathFailureStage>> failures) =>
        new(RecycledPaths, PermanentlyDeletedPaths, failures, Elapsed, LeftoverStagingDirectories);

    /// <summary>
    /// Returns a copy whose permanently deleted paths are exactly <paramref name="permanentlyDeletedPaths"/>,
    /// keeping every other part of the outcome as it was.
    /// </summary>
    /// <param name="permanentlyDeletedPaths">
    /// The paths the copy should carry as removed permanently, in the order they should be read. The gate's
    /// own removals come before the operation's, because they happened before the batch was moved at all.
    /// </param>
    internal RecycleBinResult WithPermanentlyDeleted(IReadOnlyList<string> permanentlyDeletedPaths) =>
        new(RecycledPaths, permanentlyDeletedPaths, Failures, Elapsed, LeftoverStagingDirectories);
}
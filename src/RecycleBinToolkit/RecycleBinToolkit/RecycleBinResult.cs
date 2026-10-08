namespace RecycleBinToolkit;

/// <summary>
/// Point in the recycle pipeline at which an item failed.
/// </summary>
public enum RecycleFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation,

    /// <summary>The Shell refused to produce an item for the path.</summary>
    ShellItemCreation,

    /// <summary>The item could not be added to the pending operation.</summary>
    Queueing,

    /// <summary>The Shell performed the delete and reported a failure for this item.</summary>
    Performing,

    /// <summary>The delete reported success but the result did not reach the Recycle Bin.</summary>
    ReceiptVerification,

    /// <summary>
    /// The path is too long for the Shell to recycle. Such a file would be deleted permanently while
    /// the operation still reported success, so it was never handed to the Shell.
    /// </summary>
    LongPathNotRecyclable,

    /// <summary>
    /// The volume holding the path has no Recycle Bin and the caller asked for one, so the path was
    /// refused before it was handed over and the file is where the caller left it.
    /// </summary>
    /// <remarks>
    /// This stage exists because the Shell reports success for a delete on such a volume while removing
    /// the file for good: without the refusal the caller would be told a permanent deletion had been
    /// recycled. Nothing was attempted, which is what <see cref="RecycleFailure.Attempts"/> reports as
    /// zero.
    /// <para>
    /// The refusal covers every reason the volume cannot recycle, not only the one this stage is named
    /// after — a policy, a volume set to delete outright and a volume type that never had a bin all
    /// arrive here too, and they arrive here rather than later because the Shell is never asked.
    /// </para>
    /// </remarks>
    NoRecycleBinOnVolume,

    /// <summary>
    /// The Shell did not describe an entry the Recycle Bin reported. Only reachable while reading
    /// the Recycle Bin rather than writing to it.
    /// </summary>
    Enumeration,

    /// <summary>
    /// The path was too long for the Shell and removing it outright was asked for, but the file system
    /// refused. Distinct from <see cref="LongPathNotRecyclable"/> because the length was never the
    /// obstacle here: the file is exactly as the caller left it.
    /// </summary>
    PermanentDeleteFailed
}

/// <summary>
/// One item that did not reach the Recycle Bin.
/// </summary>
public sealed class RecycleFailure
{
    /// <summary>
    /// Creates a failure record.
    /// </summary>
    /// <param name="path">The path exactly as the caller supplied it.</param>
    /// <param name="hResult">
    /// Shell error code, or 0 when the Shell was never reached. Carried through unchanged.
    /// </param>
    /// <param name="attempts">How many attempts were made before giving up.</param>
    /// <param name="stage">Where in the pipeline the failure occurred.</param>
    public RecycleFailure(string path, int hResult, int attempts, RecycleFailureStage stage)
    {
        Path = path;
        HResult = hResult;
        Attempts = attempts;
        Stage = stage;
    }

    /// <summary>The path exactly as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>
    /// The Shell's own code for this item, or 0 when the Shell was never reached. Carried through exactly
    /// as reported, so the severity bit means what the Shell meant by it.
    /// </summary>
    /// <remarks>
    /// Check <c>(HResult &amp; 0x80000000) != 0</c> to tell a refusal from a Shell success code such as
    /// <c>0x00270008</c>. That works because nothing here normalises the code, and it is worth knowing
    /// that it is not sufficient on its own: an item can carry a success code and still be a failure,
    /// which is what <see cref="Stage"/> is for. A delete the Shell completed outside the Recycle Bin is
    /// the case — the Shell reported success and the caller still needs to be told.
    /// </remarks>
    public int HResult { get; }

    /// <summary>How many attempts were made before giving up.</summary>
    public int Attempts { get; }

    /// <summary>Where in the pipeline the failure occurred.</summary>
    public RecycleFailureStage Stage { get; }
}

/// <summary>
/// Outcome of a Recycle Bin operation.
/// </summary>
public sealed class RecycleBinResult
{
    private RecycleBinResult(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<string> permanentlyDeletedPaths,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed,
        IReadOnlyList<string> leftoverStagingDirectories)
    {
        RecycledPaths = recycledPaths;
        PermanentlyDeletedPaths = permanentlyDeletedPaths;
        Failures = failures;
        Elapsed = elapsed;
        LeftoverStagingDirectories = leftoverStagingDirectories;
    }

    /// <summary>
    /// <see langword="true"/> when every requested path is gone: either recycled, or removed
    /// permanently because the caller asked for that.
    /// </summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>Paths, as supplied by the caller, that were confirmed recycled.</summary>
    public IReadOnlyList<string> RecycledPaths { get; }

    /// <summary>
    /// Paths, as supplied by the caller, that were removed permanently instead of being recycled,
    /// because their length put them past the Shell's limit and
    /// <see cref="RecycleBinOptions.LongPathFailureAction"/> was
    /// <see cref="LongPathFailureAction.DeletePermanently"/>. Empty unless that action was taken.
    /// </summary>
    public IReadOnlyList<string> PermanentlyDeletedPaths { get; }

    /// <summary>
    /// Paths that did not reach the Recycle Bin. Empty when <see cref="Succeeded"/> is
    /// <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Wall-clock time spent on the operation, including retries.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// A directory left on the volume because it still holds an item that could neither be recycled
    /// nor moved back to its original location. It is never deleted in that state, so the data stays
    /// recoverable; the caller has to move the item out of it.
    /// </summary>
    /// <remarks>
    /// Only reachable with <see cref="LongPathFailureAction.ShortenPathAndRecycle"/>, and only when
    /// both the recycle and the move back failed.
    /// <para>
    /// The first entry of <see cref="LeftoverStagingDirectories"/>, or <see langword="null"/> when there
    /// are none. Kept as its own property because a leftover on one volume does not make a leftover on
    /// another go away, so a batch spanning volumes needs the whole list; read that one when you have
    /// more than one volume in play.
    /// </para>
    /// </remarks>
    public string? LeftoverStagingDirectory => LeftoverStagingDirectories.Count > 0
        ? LeftoverStagingDirectories[0]
        : null;

    /// <summary>
    /// Every directory left on a volume because it still holds an item the operation could neither
    /// recycle nor move back, in the order they were created. Empty when nothing was left behind.
    /// </summary>
    /// <remarks>
    /// Each entry holds the caller's own data: nothing is deleted to tidy up, so an item here has to be
    /// moved out by the caller rather than recovered from somewhere else. A batch that spans volumes
    /// stages per volume, so more than one can be left behind at once and all of them are named.
    /// </remarks>
    public IReadOnlyList<string> LeftoverStagingDirectories { get; }

    /// <summary>
    /// The first recorded failure, or <see langword="null"/> when the operation succeeded.
    /// </summary>
    public RecycleFailure? FirstFailure => Failures.Count > 0 ? Failures[0] : null;

    /// <summary>
    /// Creates a result.
    /// </summary>
    /// <param name="recycledPaths">Paths confirmed recycled, in caller-supplied form.</param>
    /// <param name="permanentlyDeletedPaths">Paths removed permanently instead of recycled.</param>
    /// <param name="failures">Paths that were not handled at all.</param>
    /// <param name="elapsed">Wall-clock time spent on the operation.</param>
    /// <param name="leftoverStagingDirectories">
    /// Staging directories that still hold an item the operation could neither recycle nor move back,
    /// empty when everything was cleaned up.
    /// </param>
    public static RecycleBinResult Create(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<string> permanentlyDeletedPaths,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed,
        IReadOnlyList<string>? leftoverStagingDirectories = null) =>
        new(recycledPaths, permanentlyDeletedPaths, failures, elapsed, leftoverStagingDirectories ?? []);

    /// <summary>
    /// Returns a copy whose failures are exactly <paramref name="failures"/>, keeping every other part of
    /// the outcome as it was.
    /// </summary>
    /// <remarks>
    /// A batch that rejects some of its paths before the Shell is reached comes back needing the two
    /// failure lists joined. Rebuilding it through the constructor that has no
    /// <see cref="PermanentlyDeletedPaths"/> or <see cref="LeftoverStagingDirectories"/> would quietly
    /// reset both, and those are the two fields a caller needs to learn that a file was removed for good
    /// or that one could not be put back.
    /// </remarks>
    /// <param name="failures">The failures the copy should carry, in the order they should be read.</param>
    internal RecycleBinResult WithFailures(IReadOnlyList<RecycleFailure> failures) =>
        new(RecycledPaths, PermanentlyDeletedPaths, failures, Elapsed, LeftoverStagingDirectories);

    /// <summary>
    /// Returns a copy whose permanently deleted paths are exactly <paramref name="permanentlyDeletedPaths"/>,
    /// keeping every other part of the outcome as it was.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="WithFailures"/>, for the paths a refusal removed for good rather than
    /// reporting as failed. The two are merged by the same operation, and rebuilding the outcome through
    /// a constructor that does not take every part would reset
    /// <see cref="LeftoverStagingDirectories"/> — the list a caller reads to find data of its own that
    /// the operation could not put back.
    /// </remarks>
    /// <param name="permanentlyDeletedPaths">
    /// The paths the copy should carry as removed permanently, in the order they should be read.
    /// </param>
    internal RecycleBinResult WithPermanentlyDeleted(IReadOnlyList<string> permanentlyDeletedPaths) =>
        new(RecycledPaths, permanentlyDeletedPaths, Failures, Elapsed, LeftoverStagingDirectories);

    /// <summary>
    /// Creates a result with nothing removed permanently.
    /// </summary>
    /// <param name="recycledPaths">Paths confirmed recycled, in caller-supplied form.</param>
    /// <param name="failures">Paths that were not handled at all.</param>
    /// <param name="elapsed">Wall-clock time spent on the operation.</param>
    public static RecycleBinResult Create(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed) => new(recycledPaths, [], failures, elapsed, []);
}
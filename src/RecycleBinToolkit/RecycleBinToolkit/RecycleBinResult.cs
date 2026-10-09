namespace RecycleBinToolkit;

/// <summary>
/// Point in the recycle pipeline at which an item failed.
/// </summary>
/// <remarks>
/// The members are numbered explicitly because this is a public enum and the numbers are part of its
/// contract: an implicit value renumbers every member declared after an insertion, which compiles
/// everywhere and silently breaks every caller that persisted one. Adding a stage is therefore a matter
/// of picking the next number, and of nothing else.
/// </remarks>
public enum RecycleFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation = 0,

    /// <summary>The Shell refused to produce an item for the path.</summary>
    ShellItemCreation = 1,

    /// <summary>The item could not be added to the pending operation.</summary>
    Queueing = 2,

    /// <summary>The Shell performed the delete and reported a failure for this item.</summary>
    Performing = 3,

    /// <summary>The delete reported success but the result did not reach the Recycle Bin.</summary>
    ReceiptVerification = 4,

    /// <summary>
    /// The path is too long for the Shell to recycle. Such a file would be deleted permanently while
    /// the operation still reported success, so it was never handed to the Shell.
    /// </summary>
    LongPathNotRecyclable = 5,

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
    NoRecycleBinOnVolume = 8,

    /// <summary>
    /// The Shell did not describe an entry the Recycle Bin reported. Only reachable while reading
    /// the Recycle Bin rather than writing to it.
    /// </summary>
    Enumeration = 6,

    /// <summary>
    /// The caller asked for a path to be removed for good — because it was too long for the Shell and
    /// <see cref="RecycleBinOptions.LongPathFailureAction"/> was
    /// <see cref="LongPathFailureAction.DeletePermanently"/>, or because its volume cannot recycle it and
    /// <see cref="RecycleBinOptions.NoRecycleBinAction"/> was
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> — but the file system refused.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="LongPathNotRecyclable"/> because neither obstacle was the length: the file
    /// is exactly as the caller left it, and the caller is the one who asked for the removal to happen at
    /// all.
    /// </remarks>
    PermanentDeleteFailed = 7,

    /// <summary>
    /// The path names its volume by GUID, and nothing above this library can read such a path: the Shell's
    /// item parser rejects the form and the volume has no drive letter to be measured, emptied or reported
    /// against. It was refused before either was asked.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoRecycleBinOnVolume"/> because that one is a statement about a volume
    /// this library can name — a policy, a per-volume setting, a volume type that never had a bin. Here
    /// there is no such statement to make, and reporting one would send an integrator after a volume that
    /// very likely recycles perfectly well under the spelling they did not use.
    /// <para>
    /// Nothing was attempted, which is what <see cref="RecycleFailure.Attempts"/> reports as zero.
    /// </para>
    /// </remarks>
    VolumeGuidPathNotAddressable = 9
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

    /// <summary>
    /// Shell rounds this path went through before it was given up on, which is
    /// <see cref="RecycleBinOptions.MaxAttempts"/> at most.
    /// </summary>
    /// <remarks>
    /// <c>0</c> means the Shell was never asked about this path, which covers every stage that settles
    /// before the rounds begin: a path that never validated, one too long to hand over, one whose volume
    /// cannot recycle it, one naming its volume by GUID, and one this library removed for good on the
    /// caller's instruction. The last four are not failed rounds — nothing was attempted that could have
    /// succeeded.
    /// </remarks>
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
    /// <remarks>
    /// A path is gone when it is where the caller left it, or nowhere at all. An item that had to be moved
    /// aside to reach the Shell and could be neither recycled nor moved back is neither: it is the
    /// caller's data at a path the caller never chose, so a result carrying one is not a success even
    /// though nothing was recorded as a failure against any path. The directories holding it are named in
    /// <see cref="LeftoverStagingDirectories"/>, and a caller who reads this property as the whole answer
    /// needs to read those too — which is why this one says so rather than answering <see langword="true"/>.
    /// <para>
    /// The empty-failures case is reachable, and not only through an item left behind: a staging directory
    /// that could not be removed is reported as leftover whether or not anything is still in it, and two
    /// immediate retries are all it gets. An operation where every path was recycled can therefore report
    /// one leftover and no failures.
    /// </para>
    /// </remarks>
    public bool Succeeded => Failures.Count == 0 && LeftoverStagingDirectories.Count == 0;

    /// <summary>Paths, as supplied by the caller, that were confirmed recycled.</summary>
    public IReadOnlyList<string> RecycledPaths { get; }

    /// <summary>
    /// Paths, as supplied by the caller, that were removed permanently instead of being recycled:
    /// because their length put them past the Shell's limit and
    /// <see cref="RecycleBinOptions.LongPathFailureAction"/> was
    /// <see cref="LongPathFailureAction.DeletePermanently"/>, or because their volume cannot recycle them
    /// and <see cref="RecycleBinOptions.NoRecycleBinAction"/> was
    /// <see cref="NoRecycleBinAction.DeletePermanently"/>. Empty unless one of those was taken, and a path
    /// in here is never also in <see cref="RecycledPaths"/>.
    /// </summary>
    public IReadOnlyList<string> PermanentlyDeletedPaths { get; }

    /// <summary>
    /// Paths that did not reach the Recycle Bin. Empty when <see cref="Succeeded"/> is
    /// <see langword="true"/>, and empty of that guarantee the other way round: a result can be
    /// unsuccessful with nothing recorded here when an item was left in a staging directory.
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
    /// The first recorded failure, or <see langword="null"/> when none was. An unsuccessful result can
    /// still have none: see <see cref="Succeeded"/>.
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
    /// The counterpart of <see cref="WithFailures"/>, for the paths removed for good on the way in rather
    /// than reported as failures. The two are merged by the same operation, and rebuilding the outcome
    /// through a constructor that does not take every part would reset
    /// <see cref="LeftoverStagingDirectories"/> — the list a caller reads to find data of its own that
    /// the operation could not put back.
    /// </remarks>
    /// <param name="permanentlyDeletedPaths">
    /// The paths the copy should carry as removed permanently, in the order they should be read. The gate's
    /// own removals come before the operation's, because they happened before the batch reached the Shell
    /// at all.
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
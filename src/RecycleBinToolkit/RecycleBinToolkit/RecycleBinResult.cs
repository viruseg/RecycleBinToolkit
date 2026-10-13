namespace RecycleBinToolkit;

/// <summary>Where in the pipeline an item failed.</summary>
/// <remarks>
/// Values are numbered explicitly and the numbers are part of the contract: an implicit value renumbers every
/// member declared after an insertion, which compiles everywhere and breaks every caller that persisted one.
/// The gaps are stages the removed Shell path reported and are never reused.
/// </remarks>
public enum RecycleFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation = 0,

    /// <summary>The move to the Recycle Bin failed for this item.</summary>
    Performing = 3,

    /// <summary>
    /// The volume cannot recycle the path and the caller asked for a Recycle Bin. Nothing was attempted:
    /// <see cref="RecycleFailure.Attempts"/> is <c>0</c> and the file is untouched.
    /// </summary>
    NoRecycleBinOnVolume = 8,

    /// <summary>
    /// An entry in the Recycle Bin could not be read or removed. Reachable only from
    /// <see cref="RecycleBin.GetItemsAsync"/> and <see cref="RecycleBin.EmptyAsync"/>.
    /// </summary>
    Enumeration = 6,

    /// <summary>
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> was asked for and the file system refused. The file
    /// is untouched.
    /// </summary>
    PermanentDeleteFailed = 7,

    /// <summary>
    /// The path names its volume by GUID, as <c>\\?\Volume{GUID}\…</c>, which has no drive letter to be
    /// measured or reported against. Nothing was attempted: <see cref="RecycleFailure.Attempts"/> is <c>0</c>.
    /// </summary>
    VolumeGuidPathNotAddressable = 9,

    /// <summary>
    /// An entry could not be restored: its payload would not move, its record would not go, or the record could
    /// not be read. Reachable only from <see cref="RecycleBin.RestoreAsync(IReadOnlyList{RecycleBinItem},
    /// RestoreOptions?, System.Threading.CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// - The entry is still in the Recycle Bin, which makes a retry worth making — except where the payload
    ///   already moved and only its record was left, where the next attempt meets a conflict.
    /// - While a directory is being merged, a file of its tree that could not be dealt with reaches this
    ///   stage under that file's own path. The directory reaches it only when the merge could not finish.
    /// - One file can account for two records under the same path. Group by path rather than counting.
    /// </remarks>
    RestoreFailed = 10,

    /// <summary>
    /// An occupied path that the caller refused to resolve by setting <see cref="RestoreFileConflict.Error"/>
    /// or <see cref="RestoreDirectoryConflict.Error"/>. Nothing on disk was touched, the entry stays in the
    /// Recycle Bin, and <see cref="RecycleFailure.Attempts"/> and <see cref="RecycleFailure.HResult"/> are
    /// both <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Every other policy settles an occupied path and reports it in
    /// <see cref="RecycleBinRestoreResult.Conflicts"/>. Two reach no conflict at all:
    /// <see cref="RestoreFileConflict.Replace"/>, which takes the occupant's place, and a <c>Merge*</c> value
    /// against a destination holding a file, which fails rather than settling anything.
    /// </remarks>
    RestoreConflictRefused = 11,

    /// <summary>
    /// A path the Recycle Bin's index record cannot name, offered and refused. Nothing was moved:
    /// <see cref="RecycleFailure.Attempts"/> is <c>0</c> and the file is untouched.
    /// </summary>
    /// <remarks>
    /// Reachable only where the record in use has a fixed-size path field. Where
    /// <see cref="LongPathFailureAction.DeletePermanently"/> was set the path is removed instead, and a
    /// refusal from the file system under it arrives as <see cref="PermanentDeleteFailed"/> rather than
    /// blamed on the length.
    /// </remarks>
    LongPathNotRecyclable = 12
}

/// <summary>One item that was not handled.</summary>
public sealed class RecycleFailure
{
    /// <summary>Creates a failure record.</summary>
    /// <param name="path">The path exactly as the caller supplied it.</param>
    /// <param name="hResult">
    /// Error code, or <c>0</c> when nothing was attempted. Carried through unchanged.
    /// </param>
    /// <param name="attempts">How many attempts were made.</param>
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

    /// <summary>The file system's own code for this item, or <c>0</c> when nothing was attempted.</summary>
    /// <remarks>
    /// Check <c>(HResult &amp; 0x80000000) != 0</c> to tell a refusal from a success code.
    /// </remarks>
    public int HResult { get; }

    /// <summary>Attempts made for this path.</summary>
    /// <remarks>
    /// <c>0</c> means nothing was attempted: a path that never validated, one whose volume cannot recycle it,
    /// one naming its volume by GUID, one deleted permanently on the caller's instruction, an entry whose
    /// record could not be read, or an entry named twice that the first had already answered.
    /// </remarks>
    public int Attempts { get; }

    /// <summary>Where in the pipeline the failure occurred.</summary>
    public RecycleFailureStage Stage { get; }
}

/// <summary>The outcome of a batch recycle.</summary>
public sealed class RecycleBinResult
{
    private RecycleBinResult(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<string> permanentlyDeletedPaths,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed)
    {
        RecycledPaths = recycledPaths;
        PermanentlyDeletedPaths = permanentlyDeletedPaths;
        Failures = failures;
        Elapsed = elapsed;
    }

    /// <summary>
    /// <see langword="true"/> when every requested path is gone from disk: recycled, or deleted permanently
    /// because the caller asked for that.
    /// </summary>
    public bool Succeeded => Failures.Count == 0;

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
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Time the operation took, including retries.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The first recorded failure, or <see langword="null"/> when there is none.</summary>
    public RecycleFailure? FirstFailure => Failures.Count > 0 ? Failures[0] : null;

    /// <summary>Creates a result.</summary>
    /// <param name="recycledPaths">Paths confirmed recycled, as supplied.</param>
    /// <param name="permanentlyDeletedPaths">Paths deleted permanently instead of recycled.</param>
    /// <param name="failures">Paths that were not handled.</param>
    /// <param name="elapsed">Time the operation took.</param>
    public static RecycleBinResult Create(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<string> permanentlyDeletedPaths,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed) =>
        new(recycledPaths, permanentlyDeletedPaths, failures, elapsed);

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
    internal RecycleBinResult WithFailures(IReadOnlyList<RecycleFailure> failures) =>
        new(RecycledPaths, PermanentlyDeletedPaths, failures, Elapsed);

    /// <summary>
    /// Returns a copy whose permanently deleted paths are exactly <paramref name="permanentlyDeletedPaths"/>,
    /// keeping every other part of the outcome as it was.
    /// </summary>
    /// <param name="permanentlyDeletedPaths">
    /// The paths the copy should carry as removed permanently, in the order they should be read. The gate's
    /// own removals come before the operation's, because they happened before the batch was moved at all.
    /// </param>
    internal RecycleBinResult WithPermanentlyDeleted(IReadOnlyList<string> permanentlyDeletedPaths) =>
        new(RecycledPaths, permanentlyDeletedPaths, Failures, Elapsed);
}
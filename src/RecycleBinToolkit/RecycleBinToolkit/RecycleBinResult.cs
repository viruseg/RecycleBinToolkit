namespace RecycleBinToolkit;

/// <summary>
/// Point in the recycle pipeline at which an item failed.
/// </summary>
/// <remarks>
/// Members are numbered explicitly and the numbers are part of the contract: an implicit value renumbers every
/// member declared after an insertion, which compiles everywhere and silently breaks every caller that
/// persisted one. The gaps — 1, 2, 4, 5 — are stages the removed Shell path reported and are never reused.
/// </remarks>
public enum RecycleFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation = 0,

    /// <summary>The move to the Recycle Bin failed for this item.</summary>
    Performing = 3,

    /// <summary>
    /// The volume holding the path has no Recycle Bin and the caller asked for one, so the path was refused
    /// before anything was attempted and the file is where the caller left it.
    /// <see cref="RecycleFailure.Attempts"/> is <c>0</c>.
    /// </summary>
    /// <remarks>
    /// The refusal covers every reason a volume cannot recycle — a policy, a volume set to delete outright, a
    /// volume type that never had a bin, an answer that could not be established — and each arrives here rather
    /// than later, because nothing is handed over.
    /// </remarks>
    NoRecycleBinOnVolume = 8,

    /// <summary>
    /// An entry in the Recycle Bin could not be read or removed. Only reachable while reading or emptying the
    /// Recycle Bin rather than writing to it.
    /// </summary>
    Enumeration = 6,

    /// <summary>
    /// The caller asked for a path to be removed for good — because its volume cannot recycle it and
    /// <see cref="RecycleBinOptions.NoRecycleBinAction"/> was
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> — but the file system refused. The file is exactly
    /// as the caller left it.
    /// </summary>
    PermanentDeleteFailed = 7,

    /// <summary>
    /// The path names its volume by GUID, and nothing above this library can read such a path: the volume has
    /// no drive letter to be measured, emptied or reported against. It was refused before anything was asked
    /// and <see cref="RecycleFailure.Attempts"/> is <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="NoRecycleBinOnVolume"/>, which is a statement about a volume this library can
    /// name. Here there is no such statement to make, and reporting one would send an integrator after a
    /// volume that very likely recycles perfectly well under the spelling they did not use.
    /// </remarks>
    VolumeGuidPathNotAddressable = 9,

    /// <summary>
    /// An item in the Recycle Bin could not be restored: its payload would not move, its record would not go,
    /// or the record could not be read to say where it belonged. Only reachable while restoring. The item is
    /// still in the Recycle Bin, which is what makes a second attempt worth making — the one exception being a
    /// payload that had already moved and only its record was left, where the next attempt meets a conflict.
    /// </summary>
    /// <remarks>
    /// One file can account for two records under the same path: a refused move is refused again when the
    /// put-back cannot move it either, and each refusal is its own event with its own code. A caller that means
    /// one failure per file has to group by path rather than count.
    /// <para>
    /// While a directory is being merged, a file of its tree that could not be dealt with reaches this stage
    /// under that file's own path; the directory itself reaches it only when the merge could not finish.
    /// </para>
    /// </remarks>
    RestoreFailed = 10,

    /// <summary>
    /// The path a restore wanted was already occupied and the caller set
    /// <see cref="RestoreFileConflict.Error"/> or <see cref="RestoreDirectoryConflict.Error"/> rather than a
    /// policy that resolves it. Nothing on disk was touched, the item stays in the Recycle Bin, and this is the
    /// only stage a conflict produces in <see cref="RecycleBinRestoreResult.Failures"/>.
    /// </summary>
    /// <remarks>
    /// Every other policy settles an occupied path and reports what it did in
    /// <see cref="RecycleBinRestoreResult.Conflicts"/>. Two reach no conflict at all:
    /// <see cref="RestoreFileConflict.Replace"/> takes the occupant's place, so a destination holding a file has
    /// nothing left to report about — though one holding a directory has nothing to overwrite and fails at
    /// <see cref="RestoreFailed"/> — and a <c>Merge*</c> value against a destination holding a file fails
    /// rather than settling anything.
    /// <para>
    /// That this is the only such entry in <see cref="RecycleBinRestoreResult.Failures"/> does not make such a
    /// restore read <see cref="RecycleBinRestoreResult.Succeeded"/>: what the restore does about everything else
    /// can still fail one, and a merge that reported what it settled and then could not deal with one file is
    /// the plain case of it. The conflicts are unchanged by that failure, and both lists carry it.
    /// </para>
    /// </remarks>
    RestoreConflictRefused = 11
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
    /// Error code, or 0 when nothing was attempted. Carried through unchanged.
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

    /// <summary>The file system's own code for this item, or 0 when nothing was attempted.</summary>
    /// <remarks>
    /// Check <c>(HResult &amp; 0x80000000) != 0</c> to tell a refusal from a success code, which works because
    /// nothing here normalises the code.
    /// </remarks>
    public int HResult { get; }

    /// <summary>
    /// Rounds this path went through before it was given up on, at most
    /// <see cref="RecycleBinOptions.MaxAttempts"/>.
    /// </summary>
    /// <remarks>
    /// <c>0</c> means nothing was attempted for this path, which covers every stage that settles before the
    /// rounds begin: a path that never validated, one whose volume cannot recycle it, one naming its volume by
    /// GUID, and one removed for good on the caller's instruction — and, while restoring, an entry whose record
    /// could not be read, and an entry the caller named twice that the first had already answered.
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
        TimeSpan elapsed)
    {
        RecycledPaths = recycledPaths;
        PermanentlyDeletedPaths = permanentlyDeletedPaths;
        Failures = failures;
        Elapsed = elapsed;
    }

    /// <summary>
    /// <see langword="true"/> when every requested path is gone: either recycled, or removed
    /// permanently because the caller asked for that.
    /// </summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>Paths, as supplied by the caller, that were confirmed recycled.</summary>
    public IReadOnlyList<string> RecycledPaths { get; }

    /// <summary>
    /// Paths, as supplied by the caller, that were removed permanently instead of being recycled:
    /// because their volume cannot recycle them and <see cref="RecycleBinOptions.NoRecycleBinAction"/>
    /// was <see cref="NoRecycleBinAction.DeletePermanently"/>. Empty unless that was taken, and a path
    /// in here is never also in <see cref="RecycledPaths"/>.
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
    /// The first recorded failure, or <see langword="null"/> when none was.
    /// </summary>
    public RecycleFailure? FirstFailure => Failures.Count > 0 ? Failures[0] : null;

    /// <summary>
    /// Creates a result.
    /// </summary>
    /// <param name="recycledPaths">Paths confirmed recycled, in caller-supplied form.</param>
    /// <param name="permanentlyDeletedPaths">Paths removed permanently instead of recycled.</param>
    /// <param name="failures">Paths that were not handled at all.</param>
    /// <param name="elapsed">Wall-clock time spent on the operation.</param>
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
    /// needs to learn that a file was removed for good.
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
namespace RecycleBinToolkit;

/// <summary>
/// Point in the recycle pipeline at which an item failed.
/// </summary>
/// <remarks>
/// The members are numbered explicitly because this is a public enum and the numbers are part of its
/// contract: an implicit value renumbers every member declared after an insertion, which compiles
/// everywhere and silently breaks every caller that persisted one. Adding a stage is therefore a matter
/// of picking the next number, and of nothing else. The gaps — 1, 2, 4, 5 — are stages the removed Shell
/// path reported; they are never reused.
/// </remarks>
public enum RecycleFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation = 0,

    /// <summary>The move to the Recycle Bin failed for this item.</summary>
    Performing = 3,

    /// <summary>
    /// The volume holding the path has no Recycle Bin and the caller asked for one, so the path was
    /// refused before anything was attempted and the file is where the caller left it.
    /// </summary>
    /// <remarks>
    /// The refusal covers every reason the volume cannot recycle — a policy, a volume set to delete
    /// outright, a volume type that never had a bin, an answer that could not be established — and every
    /// one of them arrives here rather than later, because nothing is handed over.
    /// Nothing was attempted, which is what <see cref="RecycleFailure.Attempts"/> reports as zero.
    /// </remarks>
    NoRecycleBinOnVolume = 8,

    /// <summary>
    /// An entry in the Recycle Bin could not be read or removed. Only reachable while reading
    /// or emptying the Recycle Bin rather than writing to it.
    /// </summary>
    Enumeration = 6,

    /// <summary>
    /// The caller asked for a path to be removed for good — because its volume cannot recycle it and
    /// <see cref="RecycleBinOptions.NoRecycleBinAction"/> was
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> — but the file system refused.
    /// </summary>
    /// <remarks>
    /// The file is exactly as the caller left it, and the caller is the one who asked for the removal to
    /// happen at all.
    /// </remarks>
    PermanentDeleteFailed = 7,

    /// <summary>
    /// The path names its volume by GUID, and nothing above this library can read such a path: the volume
    /// has no drive letter to be measured, emptied or reported against. It was refused before anything
    /// was asked.
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
    VolumeGuidPathNotAddressable = 9,

    /// <summary>
    /// An item in the Recycle Bin could not be restored: its payload would not move, its record would not
    /// go, or the record could not be read to say where it belonged. Only reachable while restoring.
    /// </summary>
    /// <remarks>
    /// To be reached twice over once the merge exists: while a directory is being merged, a file of its tree
    /// that could not be placed would arrive here under that file's own path rather than the directory's.
    /// <b>Not implemented yet</b> — a directory whose path is occupied arrives here as itself, under all
    /// three <c>Merge*</c> values.
    /// <para>
    /// The item is still in the Recycle Bin, which is what makes a second attempt worth making. The one
    /// exception is a payload that had already moved and only its record was left: the file is on disk
    /// then, and the next attempt meets a conflict rather than this.
    /// </para>
    /// </remarks>
    RestoreFailed = 10,

    /// <summary>
    /// The path a restore wanted was already occupied and the caller asked for that to be an error rather
    /// than a conflict to resolve.
    /// </summary>
    /// <remarks>
    /// What was in the way was not touched, and the item stays in the Recycle Bin. Every policy other than
    /// this one arrives in <see cref="RecycleBinRestoreResult.Conflicts"/> alone, so the result still
    /// succeeded and this stage is what a caller opting into <c>Error</c> gets instead.
    /// <para>
    /// The one group of policies that reports neither is a directory's three <c>Merge*</c> values, because
    /// the merge walk is not implemented: a directory onto an occupied directory arrives as a
    /// <see cref="RestoreFailed"/> and lands in no conflict list at all.
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

    /// <summary>
    /// The file system's own code for this item, or 0 when nothing was attempted. Carried through exactly
    /// as reported.
    /// </summary>
    /// <remarks>
    /// Check <c>(HResult &amp; 0x80000000) != 0</c> to tell a refusal from a success code. That works
    /// because nothing here normalises the code.
    /// </remarks>
    public int HResult { get; }

    /// <summary>
    /// Rounds this path went through before it was given up on, which is
    /// <see cref="RecycleBinOptions.MaxAttempts"/> at most.
    /// </summary>
    /// <remarks>
    /// <c>0</c> means nothing was attempted for this path, which covers every stage that settles
    /// before the rounds begin: a path that never validated, one whose volume cannot recycle it, one
    /// naming its volume by GUID, and one this library removed for good on the caller's instruction.
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
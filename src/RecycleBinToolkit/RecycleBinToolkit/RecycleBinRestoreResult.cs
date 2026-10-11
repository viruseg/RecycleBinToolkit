namespace RecycleBinToolkit;

/// <summary>
/// The outcome of a restore: where the items came back to, what was in the way, and what could not be
/// moved.
/// </summary>
/// <remarks>
/// Each list keeps the relative order the items were handed in, so a caller reading two of them can still
/// say which came before which. It is not an index a caller can line up against the input: a list holds
/// only the items that belong in it, so one item of three may appear in none of the three.
/// <para>
/// Nothing here is undone afterwards: a directory whose merge did not finish stays merged, and this result
/// is the only place that says so. There is no rollback.
/// </para>
/// </remarks>
public sealed class RecycleBinRestoreResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="restoredPaths">
    /// The paths the items were restored to, in full. For an item that was set down under a different name,
    /// this is that new path rather than the one it was deleted from.
    /// </param>
    /// <param name="conflicts">Paths that were already occupied, and what was done about them.</param>
    /// <param name="failures">Items that could not be restored.</param>
    /// <param name="itemsRestored">Number of entries that left the Recycle Bin.</param>
    /// <param name="restoredSize">Bytes those entries carried back, as their records measured them.</param>
    /// <param name="elapsed">
    /// Wall-clock time the caller waited, from handing the work over to getting this result back.
    /// </param>
    public RecycleBinRestoreResult(
        IReadOnlyList<string> restoredPaths,
        IReadOnlyList<RecycleRestoreConflict> conflicts,
        IReadOnlyList<RecycleFailure> failures,
        long itemsRestored,
        long restoredSize,
        TimeSpan elapsed)
    {
        RestoredPaths = restoredPaths;
        Conflicts = conflicts;
        Failures = failures;
        ItemsRestored = itemsRestored;
        RestoredSize = restoredSize;
        Elapsed = elapsed;
    }

    /// <summary>
    /// Paths the items were restored to, in full, in the order they were asked for.
    /// </summary>
    /// <remarks>
    /// The path on disk, not the one inside the Recycle Bin, and for an item that was renamed the path it
    /// now has rather than the one it was deleted from. A stale path in a successful result is worse than
    /// one more thing the caller has to read.
    /// <para>
    /// An item the caller handed in that was skipped is not here — it is in <see cref="Conflicts"/> under
    /// <see cref="RecycleRestoreConflictAction.Skipped"/>. A file that was inside a directory is not such
    /// an item: under <see cref="RestoreDirectoryConflict.MergeSkip"/> the directory carries its own
    /// conflict record and a file of its tree that gave way has no record of its own anywhere in this
    /// result. The merging policies that settle each file are the exception — they answer in
    /// <see cref="Conflicts"/> one record per file, and the directory is in here.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> RestoredPaths { get; }

    /// <summary>
    /// Paths that were already occupied, and what was done about the items that wanted them.
    /// </summary>
    /// <remarks>
    /// Empty when nothing was in the way. A conflict that a <c>Skip</c> or a merging policy left alone —
    /// <see cref="RestoreFileConflict.Skip"/>, <see cref="RestoreDirectoryConflict.Skip"/> or any of the
    /// merging values — is here and not in <see cref="Failures"/>, so an item that stayed in the Recycle Bin
    /// does not make <see cref="Succeeded"/> false. Only
    /// <see cref="RestoreFileConflict.Error"/> and <see cref="RestoreDirectoryConflict.Error"/> do that.
    /// </remarks>
    public IReadOnlyList<RecycleRestoreConflict> Conflicts { get; }

    /// <summary>
    /// Items that could not be restored: the payload would not move, its record would not go, or a
    /// conflict was refused under <see cref="RestoreFileConflict.Error"/> or
    /// <see cref="RestoreDirectoryConflict.Error"/>.
    /// </summary>
    /// <remarks>
    /// Every item here is still in the Recycle Bin, which is what makes a second attempt worth making —
    /// except where the payload had already moved and only its record was left, and then the file is
    /// already on disk and the next attempt finds a conflict instead.
    /// </remarks>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>
    /// Number of entries that left the Recycle Bin. Never negative, and never more than the number of
    /// items handed in.
    /// </summary>
    public long ItemsRestored { get; }

    /// <summary>
    /// Bytes those entries carried back, taken from the sizes their own records report rather than from
    /// anything the restore measured afterwards.
    /// </summary>
    public long RestoredSize { get; }

    /// <summary>
    /// Wall-clock time the caller waited, from handing the work over to getting this result back.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// <see langword="true"/> when nothing failed.
    /// </summary>
    /// <remarks>
    /// Failures decide it, and conflicts do not: an item left in the Recycle Bin by
    /// <see cref="RestoreFileConflict.Skip"/> is an answer the caller asked for, not a failure to report
    /// it as one. So an item that was skipped leaves this <see langword="true"/>.
    /// </remarks>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>
    /// Returns a copy whose elapsed time is <paramref name="elapsed"/>, keeping every other part of the
    /// outcome as it was.
    /// </summary>
    /// <remarks>
    /// The engine runs on a thread from the pool, so the time it measures is not the time the caller waited
    /// for it: the wait for a thread to become free is the caller's and belongs in this one field, stamped
    /// by whoever handed the work over. Everything else is already decided when the copy is made, which is
    /// what keeps a half-restored batch from being described by two answers.
    /// </remarks>
    /// <param name="elapsed">The wall-clock time to carry.</param>
    internal RecycleBinRestoreResult WithElapsed(TimeSpan elapsed) =>
        new(RestoredPaths, Conflicts, Failures, ItemsRestored, RestoredSize, elapsed);
}
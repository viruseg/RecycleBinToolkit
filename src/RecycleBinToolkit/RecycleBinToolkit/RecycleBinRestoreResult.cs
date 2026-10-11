namespace RecycleBinToolkit;

/// <summary>
/// The outcome of a restore: where the items came back to, what was in the way, and what could not be
/// moved.
/// </summary>
/// <remarks>
/// Every list is in the order the items were handed in, so a caller can line an answer up against what it
/// asked for. Nothing here is undone afterwards: a directory whose merge did not finish stays merged, and
/// this result is the only place that says so. There is no rollback.
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
    /// </remarks>
    public IReadOnlyList<string> RestoredPaths { get; }

    /// <summary>
    /// Paths that were already occupied, and what was done about the items that wanted them.
    /// </summary>
    /// <remarks>
    /// Empty when nothing was in the way. A conflict that <see cref="RestoreFileConflict.Skip"/> left
    /// alone is here and not in <see cref="Failures"/>, so an item that stayed in the Recycle Bin does not
    /// make <see cref="Succeeded"/> false — only <see cref="RestoreFileConflict.Error"/> and
    /// <see cref="RestoreDirectoryConflict.Error"/> do that.
    /// </remarks>
    public IReadOnlyList<RecycleRestoreConflict> Conflicts { get; }

    /// <summary>
    /// Items that could not be restored: the payload would not move, its record would not go, or a
    /// conflict was refused under <see cref="RestoreFileConflict.Error"/>.
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
    /// <see langword="true"/> when every item asked for was restored.
    /// </summary>
    /// <remarks>
    /// Failures decide it, and conflicts do not: an item left in the Recycle Bin by
    /// <see cref="RestoreFileConflict.Skip"/> is an answer the caller asked for, not a failure to report
    /// it as one.
    /// </remarks>
    public bool Succeeded => Failures.Count == 0;
}
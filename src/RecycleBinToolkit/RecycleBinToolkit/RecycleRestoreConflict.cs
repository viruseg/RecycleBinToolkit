namespace RecycleBinToolkit;

/// <summary>
/// What a restore did about a path that was already occupied.
/// </summary>
/// <remarks>
/// One enumerator rather than one per policy, because a caller reads this to answer a single question —
/// what was in the way, and what was done about the item that wanted it. The policies differ on the answer,
/// not on the question.
/// </remarks>
public enum RecycleRestoreConflictAction
{
    /// <summary>Nothing on disk was touched and the item is still in the Recycle Bin.</summary>
    Skipped = 0,

    /// <summary>
    /// The item was set down beside the occupied path under a free name rather than at the path that was
    /// occupied.
    /// </summary>
    /// <remarks>
    /// Whether that item was a file or a directory is
    /// <see cref="RecycleRestoreConflict.OccupiedByDirectory"/>'s answer rather than this one's: a
    /// directory set down whole under a free name and a file set down beside its collided path are the same
    /// event to a caller reading the result.
    /// </remarks>
    RestoredRenamed,

    /// <summary>
    /// A merge that left a file in the way alone: the incoming file did not fit and went back into the
    /// Recycle Bin as an entry of its own.
    /// </summary>
    /// <remarks>
    /// Under <see cref="RestoreDirectoryConflict.MergeSkip"/> this is one record for the directory rather
    /// than one for each file that gave way, because nothing about those files was resolved — each is an
    /// ordinary item in the next listing.
    /// <para>
    /// <see cref="RecycleRestoreConflict.Path"/> is that directory's path, and
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/> names it too <em>when the merge finished</em>:
    /// a merge whose payload or record would not go afterwards still reports what it settled, and reports
    /// itself as a <see cref="RecycleFailureStage.RestoreFailed"/> at the same time.
    /// </para>
    /// </remarks>
    MergedSkipped,

    /// <summary>A merge that overwrote the file that was in the way.</summary>
    /// <remarks>
    /// One record per file that was overwritten, under
    /// <see cref="RestoreDirectoryConflict.MergeReplace"/>, and
    /// <see cref="RecycleRestoreConflict.Path"/> is the path it was overwritten at.
    /// </remarks>
    MergedReplaced,

    /// <summary>A merge that set the incoming file down under a free name.</summary>
    /// <remarks>
    /// One record per file that was renamed, under <see cref="RestoreDirectoryConflict.MergeRename"/>,
    /// and <see cref="RecycleRestoreConflict.Path"/> is where that file ended up rather than where it
    /// collided.
    /// </remarks>
    MergedRenamed
}

/// <summary>
/// One path that was already occupied when a restore reached it, and what was done about it.
/// </summary>
/// <remarks>
/// Reported rather than raised: a conflict is an outcome the caller asked about by choosing a policy, and
/// a batch that hit one carries on with the items after it.
/// </remarks>
public sealed class RecycleRestoreConflict
{
    /// <summary>Creates a conflict record.</summary>
    /// <param name="path">
    /// The path the conflict is about, in full — the path that was occupied, except under
    /// <see cref="RecycleRestoreConflictAction.RestoredRenamed"/> or
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/>, where it is where the item landed.
    /// </param>
    /// <param name="occupiedByDirectory">
    /// Whether what occupied the path the item wanted is a directory rather than a file. Under the renamed
    /// actions this still answers about the path the item collided with, not about the free name it was set
    /// down under — nothing occupies that one.
    /// </param>
    /// <param name="action">What was done about the item that wanted that path.</param>
    public RecycleRestoreConflict(string path, bool occupiedByDirectory, RecycleRestoreConflictAction action)
    {
        Path = path;
        OccupiedByDirectory = occupiedByDirectory;
        Action = action;
    }

    /// <summary>The path the conflict is about, in full.</summary>
    /// <remarks>
    /// The path the item wanted, and the one a caller finds on disk when they look, for every action but the
    /// two that name where the item landed instead: <see cref="RecycleRestoreConflictAction.RestoredRenamed"/>
    /// and <see cref="RecycleRestoreConflictAction.MergedRenamed"/>. Under those two this is where the item
    /// ended up and not where it collided — it was set down elsewhere on purpose, and the path it collided
    /// at is named by no record in this result. The item is in
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/> either way.
    /// </remarks>
    public string Path { get; }

    /// <summary>
    /// Whether what occupied the path the item wanted is a directory rather than a file.
    /// </summary>
    /// <remarks>
    /// The two are different conflicts — a directory can be set down whole and a file cannot — so a caller
    /// reading <see cref="RecycleBinRestoreResult.Conflicts"/> gets this without inspecting the disk to
    /// find out what it was up against.
    /// <para>
    /// Not a statement about <see cref="Path"/> under
    /// <see cref="RecycleRestoreConflictAction.RestoredRenamed"/> or
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/>: nothing occupies the free name the item was
    /// set down under, and this still answers about the path it collided with.
    /// </para>
    /// </remarks>
    public bool OccupiedByDirectory { get; }

    /// <summary>What was done about the item whose path was occupied.</summary>
    public RecycleRestoreConflictAction Action { get; }
}
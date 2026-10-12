namespace RecycleBinToolkit;

/// <summary>What a restore did about a path that was already occupied.</summary>
public enum RecycleRestoreConflictAction
{
    /// <summary>Nothing on disk was touched and the item is still in the Recycle Bin.</summary>
    Skipped = 0,

    /// <summary>The item was set down beside the occupied path under a free name.</summary>
    RestoredRenamed,

    /// <summary>
    /// A merge that left a file in the way alone: the incoming file did not fit and went back into the
    /// Recycle Bin as an entry of its own. Under
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/> this is one record for the directory rather than one
    /// per file that gave way, because nothing about those files was resolved.
    /// </summary>
    MergedSkipped,

    /// <summary>
    /// A merge that overwrote the file that was in the way, under
    /// <see cref="RestoreDirectoryConflict.MergeReplace"/>. One record per file overwritten.
    /// </summary>
    MergedReplaced,

    /// <summary>
    /// A merge that set the incoming file down under a free name, under
    /// <see cref="RestoreDirectoryConflict.MergeRename"/>. One record per file renamed.
    /// </summary>
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
    /// The path the item wanted, and the one a caller finds on disk when they look — except under
    /// <see cref="RecycleRestoreConflictAction.RestoredRenamed"/> and
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/>, where it is where the item landed and the
    /// path it collided at is named by no record in this result.
    /// <para>
    /// Under <see cref="RecycleRestoreConflictAction.RestoredRenamed"/> the item <em>is</em> the entry the
    /// caller restored and <see cref="RecycleBinRestoreResult.RestoredPaths"/> names it. Under
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/> it is a file <em>inside</em> a merged directory
    /// and that list holds the directory's path instead, so one restored entry can report several of these.
    /// </para>
    /// </remarks>
    public string Path { get; }

    /// <summary>Whether what occupied the path the item wanted is a directory rather than a file.</summary>
    /// <remarks>
    /// The two are different conflicts — a directory can be set down whole and a file cannot — so a caller
    /// reading <see cref="RecycleBinRestoreResult.Conflicts"/> gets this without going to the disk to find out
    /// what it was up against. Under either renamed action this still answers about the path the item collided
    /// with, not the free name it was set down under: nothing occupies that one.
    /// </remarks>
    public bool OccupiedByDirectory { get; }

    /// <summary>What was done about the item whose path was occupied.</summary>
    public RecycleRestoreConflictAction Action { get; }
}
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
    RestoredRenamed,

    /// <summary>
    /// The directory was merged, and the files of the same name that were already there were left as they
    /// were.
    /// </summary>
    /// <remarks>
    /// One record for the directory rather than one for each file that gave way, because nothing about
    /// those files was resolved: they went back into the Recycle Bin as entries of their own and are
    /// ordinary items in the next listing. The directory itself was restored, so
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/> names it.
    /// </remarks>
    DirectoryMergedSkip,

    /// <summary>The directory was merged, and each file of the same name was overwritten.</summary>
    DirectoryMergedReplace,

    /// <summary>
    /// The directory was merged, and each incoming file that clashed was set down under a free name of its
    /// own.
    /// </summary>
    DirectoryMergedRename,

    /// <summary>The whole directory was set down beside the occupied one under a free name.</summary>
    DirectoryRenamed
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
    /// <param name="path">The path that was already occupied, in full.</param>
    /// <param name="occupiedByDirectory">Whether what occupies <paramref name="path"/> is a directory.</param>
    /// <param name="action">What was done about the item that wanted that path.</param>
    public RecycleRestoreConflict(string path, bool occupiedByDirectory, RecycleRestoreConflictAction action)
    {
        Path = path;
        OccupiedByDirectory = occupiedByDirectory;
        Action = action;
    }

    /// <summary>
    /// The path that was already occupied, in full.
    /// </summary>
    /// <remarks>
    /// The path the item wanted, not the one it may have landed on: a renamed item's new path is in
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/>, and what was in the way is only here. It is
    /// the path the caller would find on disk when they look, which is why it is worth reporting even for
    /// the conflicts that were resolved.
    /// </remarks>
    public string Path { get; }

    /// <summary>Whether what occupies <see cref="Path"/> is a directory rather than a file.</summary>
    /// <remarks>
    /// The two are different conflicts — one directory can be merged and the other cannot — so a caller
    /// reading <see cref="RecycleBinRestoreResult.Conflicts"/> gets this without inspecting the disk to
    /// find out what it was up against.
    /// </remarks>
    public bool OccupiedByDirectory { get; }

    /// <summary>What was done about the item that wanted <see cref="Path"/>.</summary>
    public RecycleRestoreConflictAction Action { get; }
}
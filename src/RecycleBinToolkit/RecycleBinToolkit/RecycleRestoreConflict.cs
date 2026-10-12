namespace RecycleBinToolkit;

/// <summary>How a restore resolved an occupied path.</summary>
public enum RecycleRestoreConflictAction
{
    /// <summary>Nothing on disk was touched and the entry is still in the Recycle Bin.</summary>
    Skipped = 0,

    /// <summary>The entry was set down beside the occupied path under a free name.</summary>
    RestoredRenamed,

    /// <summary>
    /// A merge that left a colliding file alone; the incoming file went back into the Recycle Bin as an entry of
    /// its own. Under <see cref="RestoreDirectoryConflict.MergeSkip"/> this is one record for the directory.
    /// </summary>
    MergedSkipped,

    /// <summary>
    /// A merge that overwrote a colliding file, under <see cref="RestoreDirectoryConflict.MergeReplace"/>. One
    /// record per file.
    /// </summary>
    MergedReplaced,

    /// <summary>
    /// A merge that set an incoming file down under a free name, under
    /// <see cref="RestoreDirectoryConflict.MergeRename"/>. One record per file.
    /// </summary>
    MergedRenamed
}

/// <summary>One occupied path a restore reached, and how it was resolved.</summary>
/// <remarks>
/// Reported rather than raised, and the restore carries on with the entries after it.
/// </remarks>
public sealed class RecycleRestoreConflict
{
    /// <summary>Creates a conflict record.</summary>
    /// <param name="path">The path the conflict is about, in full.</param>
    /// <param name="occupiedByDirectory">
    /// Whether what occupied the path is a directory rather than a file. Under either renamed action this answers
    /// about the collided path, not the free name.
    /// </param>
    /// <param name="action">How the conflict was resolved.</param>
    public RecycleRestoreConflict(string path, bool occupiedByDirectory, RecycleRestoreConflictAction action)
    {
        Path = path;
        OccupiedByDirectory = occupiedByDirectory;
        Action = action;
    }

    /// <summary>The path the conflict is about, in full.</summary>
    /// <remarks>
    /// The path the entry wanted, except under <see cref="RecycleRestoreConflictAction.RestoredRenamed"/> and
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/>, where it is where the entry landed and the
    /// collided path is named by no record in the result. Under
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/> it names a file inside a merged directory, so
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/> holds the directory's path instead and one restored
    /// entry can report several of these.
    /// </remarks>
    public string Path { get; }

    /// <summary>Whether what occupied the path is a directory rather than a file.</summary>
    public bool OccupiedByDirectory { get; }

    /// <summary>How the conflict was resolved.</summary>
    public RecycleRestoreConflictAction Action { get; }
}
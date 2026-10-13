namespace RecycleBinToolkit;

/// <summary>What a restore does with a directory whose original path is occupied.</summary>
/// <remarks>
/// No value writes outside the directory the caller named, and nothing already on disk is renamed or moved.
/// A destination occupied by a <em>file</em> is a conflict like any other: the merge values have no tree to
/// merge into, so the move is refused and reported at <see cref="RestoreFailureStage.RestoreFailed"/>, where
/// <see cref="RenameDirectory"/> takes a free name instead.
/// </remarks>
public enum RestoreDirectoryConflict
{
    /// <summary>
    /// Merge the tree into what is already there, leaving every file of the same name as it is. The default.
    /// A file that does not fit goes back into the Recycle Bin as an entry of its own. Reported as one
    /// <see cref="RecycleRestoreConflictAction.MergedSkipped"/> for the directory.
    /// </summary>
    MergeSkip = 0,

    /// <summary>
    /// The same merge, overwriting every file of the same name. A subdirectory of the same name is merged
    /// into rather than replaced. Reported as one <see cref="RecycleRestoreConflictAction.MergedReplaced"/>
    /// per file overwritten.
    /// </summary>
    MergeReplace,

    /// <summary>
    /// The same merge, setting each colliding file down under a free name of its own, so <c>report.txt</c>
    /// becomes <c>report (1).txt</c>. Reported as one <see cref="RecycleRestoreConflictAction.MergedRenamed"/>
    /// per file renamed.
    /// </summary>
    MergeRename,

    /// <summary>Set the whole tree down beside the one already there under a free name, merging nothing.</summary>
    RenameDirectory,

    /// <summary>
    /// Leave the entry in the Recycle Bin and touch nothing on disk — not even missing parent directories.
    /// </summary>
    Skip,

    /// <summary>
    /// As <see cref="Skip"/>, and the entry is also reported in
    /// <see cref="RecycleBinRestoreResult.Failures"/>, which makes
    /// <see cref="RecycleBinRestoreResult.Succeeded"/> <see langword="false"/>.
    /// </summary>
    Error
}

/// <summary>What a restore does with a file whose original path is occupied.</summary>
public enum RestoreFileConflict
{
    /// <summary>
    /// Leave the entry in the Recycle Bin and touch nothing on disk. The default. The incoming file is the
    /// one that gives way.
    /// </summary>
    Skip = 0,

    /// <summary>Overwrite the file already there. Does not extend to a directory, which is never replaced.</summary>
    Replace,

    /// <summary>
    /// Set the file down beside the occupied path under the first free name, so <c>report.txt</c> becomes
    /// <c>report (1).txt</c>. The number starts at one and the extension is kept, splitting on the last dot,
    /// so <c>archive.tar.gz</c> becomes <c>archive.tar (1).gz</c>. Nothing already on disk is renamed, moved
    /// or removed.
    /// </summary>
    Rename,

    /// <summary>
    /// As <see cref="Skip"/>, and the entry is also reported in
    /// <see cref="RecycleBinRestoreResult.Failures"/>, which makes
    /// <see cref="RecycleBinRestoreResult.Succeeded"/> <see langword="false"/>.
    /// </summary>
    Error
}
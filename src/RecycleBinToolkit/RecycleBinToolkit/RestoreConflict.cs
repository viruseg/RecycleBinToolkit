namespace RecycleBinToolkit;

/// <summary>
/// What to do with a directory whose original path is already occupied when a restore reaches it.
/// </summary>
/// <remarks>
/// A question of its own rather than a variant of <see cref="RestoreFileConflict"/>, because a directory
/// and a file do not have the same choices: a directory can be set down whole under a free name and a file
/// only ever under its own, and merging a tree into what is already there is a question a file cannot be
/// asked. One enumerator between them would carry values that are impossible for one of the two.
/// <para>
    /// No value here writes outside the directory the caller named, and none touches a directory the restore
    /// was not restoring. What is already on disk is left alone, overwritten, or set beside — never renamed,
    /// moved or removed.
    /// </para>
/// <para>
    /// A destination occupied by a <em>file</em> is a conflict like any other, and the three merge values
    /// have no tree to merge into: the move is refused and the entry is reported at
    /// <see cref="RecycleFailureStage.RestoreFailed"/> with the file left exactly as it was, where
    /// <see cref="RenameDirectory"/> takes a free name instead.
    /// </para>
    /// </remarks>
public enum RestoreDirectoryConflict
{
    /// <summary>
    /// Merge the directory's contents into the one already there, leaving every file of the same name
    /// that is already there exactly as it is.
    /// </summary>
    /// <remarks>
    /// The default, and for the same reason <see cref="RestoreFileConflict.Skip"/> is: the incoming file
    /// is the one that gives way, so what is already on disk is never what is lost.
    /// <para>
    /// A file that does not fit is not left inside the payload the directory came in, because that payload is
    /// removed once the directory has been dealt with and the file would go with it. It goes back into the
    /// Recycle Bin as an entry of its own instead: an ordinary item in the next listing, under the path it
    /// wanted, restorable later by the same call once the squatter has moved on.
    /// </para>
    /// <para>
    /// Reported as one <see cref="RecycleRestoreConflictAction.MergedSkipped"/> for the directory rather than
    /// one per file that gave way, because nothing about those files was resolved here — each is an entry in
    /// the next listing, and the directory is in
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/>. The merge itself reaches no failure stage.
    /// </para>
    /// </remarks>
    MergeSkip = 0,

    /// <summary>
    /// Merge the directory's contents into the one already there, overwriting every file of the same name
    /// that is already there.
    /// </summary>
    /// <remarks>
    /// A subdirectory of the same name is merged into rather than replaced: the only way to replace a
    /// directory is to delete what is there first and find out afterwards whether the move was possible at
    /// all, and the bytes destroyed by that are the caller's. Overwriting a file is the caller's own
    /// instruction and happens on the caller's file; it does not extend to directories, which is why there
    /// is no value here that replaces one whole.
    /// <para>
    /// Reported as one <see cref="RecycleRestoreConflictAction.MergedReplaced"/> per file that was
    /// overwritten, under the path it was overwritten at, and reaches no failure stage.
    /// </para>
    /// </remarks>
    MergeReplace,

    /// <summary>
    /// Merge the directory's contents into the one already there, setting each incoming file that clashes
    /// down under a free name of its own — <c>report.txt</c> becoming <c>report (1).txt</c>.
    /// </summary>
    /// <remarks>
    /// Only what comes out of the Recycle Bin is ever renamed. What is already on disk is not renamed,
    /// moved or removed.
    /// <para>
    /// Reported as one <see cref="RecycleRestoreConflictAction.MergedRenamed"/> per file that was renamed,
    /// under the free name it landed at rather than the one it collided at, and reaches no failure stage.
    /// </para>
    /// </remarks>
    MergeRename,

    /// <summary>
    /// Set the whole directory down beside the one already there under a free name, contents merged with
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The tree arrives as it was, so nothing inside it has to agree with anything outside it. The caller
    /// finds the name in <see cref="RecycleBinRestoreResult.RestoredPaths"/> and the path that was
    /// occupied in <see cref="RecycleBinRestoreResult.Conflicts"/>.
    /// </remarks>
    RenameDirectory,

    /// <summary>Leave the item in the Recycle Bin and touch nothing on disk.</summary>
    /// <remarks>
    /// Not even the missing parent directories are created: an item that is not put back leaves nothing
    /// behind, and a caller who wants the path prepared has to say so by restoring something into it.
    /// </remarks>
    Skip,

    /// <summary>
    /// Leave the item in the Recycle Bin and touch nothing on disk, and report the item as a failure as
    /// well as a conflict.
    /// </summary>
    /// <remarks>
    /// The whole difference from <see cref="Skip"/> is that one line of the answer: the operation carries
    /// on with the next item either way, but <see cref="RecycleBinRestoreResult.Succeeded"/> is
    /// <see langword="false"/> here and <see langword="true"/> there. Use it for a caller that treats an
    /// occupied path as something to be told about rather than as an outcome to proceed from.
    /// </remarks>
    Error
}

/// <summary>
/// What to do with a file whose original path is already occupied when a restore reaches it.
/// </summary>
/// <remarks>
/// Every value either leaves what is on disk alone, writes over it, or sets the incoming file down beside
/// it under a name this library picked. None writes outside the directory the caller named, and none
/// touches a file the restore was not restoring.
/// </remarks>
public enum RestoreFileConflict
{
    /// <summary>Leave the item in the Recycle Bin and touch nothing on disk.</summary>
    /// <remarks>
    /// The default, and the only value that cannot destroy anything the caller did not ask to destroy: the
    /// incoming file is the one that gives way, and it is still in the Recycle Bin afterwards.
    /// </remarks>
    Skip = 0,

    /// <summary>Overwrite the file that is already there.</summary>
    /// <remarks>
    /// Whatever occupied the path is gone the moment this returns. That is the caller's own file and the
    /// caller's own instruction; it does not extend to a directory, which is never replaced whole.
    /// </remarks>
    Replace,

    /// <summary>
    /// Set the file down beside the occupied path under the first free name — <c>report.txt</c> becoming
    /// <c>report (1).txt</c>.
    /// </summary>
    /// <remarks>
    /// The number starts at one and the extension is kept, splitting the name on the last dot rather than
    /// the first, so <c>archive.tar.gz</c> becomes <c>archive.tar (1).gz</c>. What is already on disk is
    /// not renamed, moved or removed, and the caller reads where the file landed from
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/>.
    /// </remarks>
    Rename,

    /// <summary>
    /// Leave the item in the Recycle Bin and touch nothing on disk, and report the item as a failure as
    /// well as a conflict.
    /// </summary>
    /// <remarks>
    /// The whole difference from <see cref="Skip"/> is that one line of the answer: the operation carries
    /// on with the next item either way, but <see cref="RecycleBinRestoreResult.Succeeded"/> is
    /// <see langword="false"/> here and <see langword="true"/> there.
    /// </remarks>
    Error
}
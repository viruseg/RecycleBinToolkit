namespace RecycleBinToolkit;

/// <summary>
/// What to do with a directory whose original path is already occupied when a restore reaches it.
/// </summary>
/// <remarks>
/// No value here writes outside the directory the caller named, and none touches a directory the restore was
/// not restoring. What is already on disk is left alone, overwritten, or set beside — never renamed, moved,
/// or removed outright.
/// <para>
/// A destination occupied by a <em>file</em> is a conflict like any other, and the merge values have no tree
/// to merge into: the move is refused and reported at
/// <see cref="RecycleFailureStage.RestoreFailed"/> with the file left exactly as it was, where
/// <see cref="RenameDirectory"/> takes a free name instead.
/// </para>
/// </remarks>
public enum RestoreDirectoryConflict
{
    /// <summary>
    /// Merge the tree into what is already there, leaving every file of the same name that is already
    /// there exactly as it is. The default. A file that does not fit goes back into the Recycle Bin as an
    /// entry of its own, under the path it wanted, rather than being lost with the payload it was in.
    /// Reported as one <see cref="RecycleRestoreConflictAction.MergedSkipped"/> for the directory.
    /// </summary>
    MergeSkip = 0,

    /// <summary>
    /// The same merge, overwriting every file of the same name that is already there. A subdirectory of the
    /// same name is merged into rather than replaced — the only way to replace a directory is to delete what
    /// is there first and find out afterwards whether the move was possible. Reported as one
    /// <see cref="RecycleRestoreConflictAction.MergedReplaced"/> per file overwritten, under the path it was
    /// overwritten at.
    /// </summary>
    MergeReplace,

    /// <summary>
    /// The same merge, setting each incoming file that clashes down under a free name of its own, so
    /// <c>report.txt</c> becomes <c>report (1).txt</c>. Reported as one
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/> per file renamed, under the name it landed
    /// at rather than the one it collided at.
    /// </summary>
    MergeRename,

    /// <summary>
    /// Set the whole tree down beside the one already there under a free name, merged with nothing. The
    /// tree arrives as it was, so nothing inside it has to agree with anything outside it.
    /// </summary>
    RenameDirectory,

    /// <summary>
    /// Leave the entry in the Recycle Bin and touch nothing on disk — not even the missing parent
    /// directories, so an item that is not put back leaves nothing behind.
    /// </summary>
    Skip,

    /// <summary>
    /// As <see cref="Skip"/>, and the entry is reported in <see cref="RecycleBinRestoreResult.Failures"/>
    /// as well. That one list is the whole difference, and <see cref="RecycleBinRestoreResult.Succeeded"/>
    /// follows it, so a restore that did nothing else wrong reads <see langword="false"/> here and
    /// <see langword="true"/> under <see cref="Skip"/>.
    /// </summary>
    Error
}

/// <summary>
/// What to do with a file whose original path is already occupied when a restore reaches it.
/// </summary>
/// <remarks>
/// Every value either leaves what is on disk alone, writes over it, or sets the incoming file down beside it
/// under a name this library picked. None writes outside the directory the caller named, and the one file
/// here that is not the one being restored — what already occupies the path — is overwritten by
/// <see cref="Replace"/> and left alone by every other value.
/// </remarks>
public enum RestoreFileConflict
{
    /// <summary>
    /// Leave the entry in the Recycle Bin and touch nothing on disk. The default. The incoming file is the
    /// one that gives way, and it is still there afterwards.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Overwrite the file already there, which is then gone the moment this returns. The caller's own file
    /// and the caller's own instruction; it does not extend to a directory, which is never replaced whole.
    /// </summary>
    Replace,

    /// <summary>
    /// Set the file down beside the occupied path under the first free name, so <c>report.txt</c> becomes
    /// <c>report (1).txt</c>. The number starts at one and the extension is kept, splitting on the last dot
    /// rather than the first, so <c>archive.tar.gz</c> becomes <c>archive.tar (1).gz</c>. Nothing already on
    /// disk is renamed, moved or removed.
    /// </summary>
    Rename,

    /// <summary>
    /// As <see cref="Skip"/>, and the entry is reported in <see cref="RecycleBinRestoreResult.Failures"/>
    /// as well. That one list is the whole difference, and <see cref="RecycleBinRestoreResult.Succeeded"/>
    /// follows it, so a restore that did nothing else wrong reads <see langword="false"/> here and
    /// <see langword="true"/> under <see cref="Skip"/>.
    /// </summary>
    Error
}
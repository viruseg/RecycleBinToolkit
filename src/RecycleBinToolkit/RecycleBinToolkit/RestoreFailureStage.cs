namespace RecycleBinToolkit;

/// <summary>Where a <see cref="RecycleBin.RestoreAsync(IReadOnlyList{RecycleBinItem}, RestoreOptions?, CancellationToken)"/> call stopped for one entry.</summary>
public enum RestoreFailureStage
{
    /// <summary>
    /// The entry could not be restored: its payload would not move, its record would not go, or the record
    /// could not be read.
    /// </summary>
    /// <remarks>
    /// - The entry is still in the Recycle Bin, which makes a retry worth making — except where the payload
    ///   already moved and only its record was left, where the next attempt meets a conflict.
    /// - While a directory is being merged, a file of its tree that could not be dealt with reaches this
    ///   stage under that file's own path. The directory reaches it only when the merge could not finish.
    /// - One file can account for two records under the same path. Group by path rather than counting.
    /// </remarks>
    RestoreFailed,

    /// <summary>
    /// An occupied path that the caller refused to resolve by setting <see cref="RestoreFileConflict.Error"/>
    /// or <see cref="RestoreDirectoryConflict.Error"/>. Nothing on disk was touched, the entry stays in the
    /// Recycle Bin, and <see cref="RecycleFailure{TStage}.Attempts"/> and
    /// <see cref="RecycleFailure{TStage}.HResult"/> are both <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Every other policy settles an occupied path and reports it in
    /// <see cref="RecycleBinRestoreResult.Conflicts"/>. Two reach no conflict at all:
    /// <see cref="RestoreFileConflict.Replace"/>, which takes the occupant's place, and a <c>Merge*</c> value
    /// against a destination holding a file, which fails rather than settling anything.
    /// </remarks>
    RestoreConflictRefused
}
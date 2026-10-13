namespace RecycleBinToolkit;

/// <summary>The outcome of one <see cref="RecycleBin.RestoreAsync(IReadOnlyList{RecycleBinItem}, RestoreOptions?, CancellationToken)"/> call.</summary>
/// <remarks>
/// Each list keeps the relative order the entries were handed in, and none of them is an index the caller can
/// line up against the input: a list holds only the entries that belong in it, so one entry of three may appear
/// in none of the three.
/// <para>
/// Nothing here is rolled back. An entry that was overwritten, or set down under a free name, stays that way,
/// and this result is the only record of it.
/// </para>
/// </remarks>
public sealed class RecycleBinRestoreResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="restoredPaths">The paths the entries were restored to, in full.</param>
    /// <param name="conflicts">Occupied paths, and what was done about them.</param>
    /// <param name="failures">Entries that were not restored.</param>
    /// <param name="itemsRestored">Number of entries that left the Recycle Bin.</param>
    /// <param name="restoredSize">Total size in bytes of those entries.</param>
    /// <param name="elapsed">Time the call took.</param>
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

    /// <summary>Where each entry was restored to, in full.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>An entry put back where its record said is named under that record's own spelling, which may end in
    /// a directory separator, while the move itself is given the trimmed name. So
    /// <see cref="File.Exists(string)"/> answers <see langword="false"/> of a reported directory that has one,
    /// where <see cref="Directory.Exists(string)"/> answers <see langword="true"/>.</item>
    /// <item>An entry set down under a free name is named under the name it now has.</item>
    /// <item>A skipped entry is not here — it is in <see cref="Conflicts"/> under
    /// <see cref="RecycleRestoreConflictAction.Skipped"/>.</item>
    /// <item>A file inside a merged directory is not here either: the directory carries its own record and is
    /// itself in this list.</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<string> RestoredPaths { get; }

    /// <summary>Occupied paths, and how each was resolved.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>One record per entry handed in, except under the three merge policies, which report each file the
    /// merge settled — one <see cref="RecycleRestoreConflictAction.MergedSkipped"/> for the whole directory
    /// under <see cref="RestoreDirectoryConflict.MergeSkip"/>, and one
    /// <see cref="RecycleRestoreConflictAction.MergedReplaced"/> or
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/> per file under the other two.</item>
    /// <item>A tree that collides with nothing reports nothing.</item>
    /// <item><see cref="RestoreFileConflict.Replace"/> reports nothing: it resolves its own conflict by taking
    /// the occupant's place.</item>
    /// <item>Nothing here affects <see cref="Succeeded"/>. A restore that reported a conflict can still read
    /// <see langword="false"/>, and a merge that reported what it settled and then failed on one file carries
    /// records in both this list and <see cref="Failures"/>.</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<RecycleRestoreConflict> Conflicts { get; }

    /// <summary>Entries that were not restored.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Every entry here is still in the Recycle Bin, which makes a retry worth making — except where the
    /// payload had already moved and only its record was left, and then the file is already on disk.</item>
    /// <item>A merge reports a refused file under that file's own path, alongside the directory's own outcome.</item>
    /// <item>One file can account for two records under the same path. Group by path for one failure per
    /// file.</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Number of entries that left the Recycle Bin.</summary>
    public long ItemsRestored { get; }

    /// <summary>Total size in bytes of the restored entries, from their own records.</summary>
    public long RestoredSize { get; }

    /// <summary>Time the call took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary><see langword="true"/> when <see cref="Failures"/> is empty.</summary>
    /// <remarks>
    /// Conflicts do not affect it: an entry left in the Recycle Bin by <see cref="RestoreFileConflict.Skip"/>
    /// is an answer the caller asked for, not a failure.
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
namespace RecycleBinToolkit;

/// <summary>
/// The outcome of a restore: where the items came back to, what was in the way, and what could not be
/// moved.
/// </summary>
/// <remarks>
/// Each list keeps the relative order the items were handed in, so a caller reading two of them can still
/// say which came before which. It is not an index a caller can line up against the input: a list holds
/// only the items that belong in it, so one item of three may appear in none of the three.
/// <para>
/// Nothing here is undone afterwards: an item that was overwritten, or set down under a free name beside
/// what was in its way, stays that way, and this result is the only place that says so. There is no
/// rollback.
/// </para>
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

    /// <summary>Paths the items were restored to, in full, in the order they were asked for.</summary>
    /// <remarks>
    /// The path on disk, not the one inside the Recycle Bin — and for an item set down under a free name, the
    /// path it now has rather than the one it was deleted from.
    /// <para>
    /// An item put back where its record said is named under that record's own spelling, which is the spelling
    /// the caller used on the day it was deleted and may end in a directory separator, while the move itself is
    /// given the trimmed name. So <see cref="File.Exists(string)"/> answers <see langword="false"/> of a reported
    /// directory that has one where <see cref="Directory.Exists(string)"/> answers <see langword="true"/>.
    /// </para>
    /// <para>
    /// An item that was skipped is not here — it is in <see cref="Conflicts"/> under
    /// <see cref="RecycleRestoreConflictAction.Skipped"/>. Neither is a file inside a directory that was
    /// merged: under <see cref="RestoreDirectoryConflict.MergeSkip"/> the directory carries its own conflict
    /// record and a file of its tree that gave way has no record anywhere in this result, being an ordinary
    /// entry in the next listing. The directory itself is here, because it was restored.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> RestoredPaths { get; }

    /// <summary>
    /// Paths that were already occupied, and what was done about the items that wanted them.
    /// </summary>
    /// <remarks>
    /// Empty when nothing was in the way. One record per entry the caller handed in, except under the three
    /// merge policies: a merge settles one entry of a tree at a time and reports each it settled — one
    /// <see cref="RecycleRestoreConflictAction.MergedSkipped"/> for the directory under
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/>, one
    /// <see cref="RecycleRestoreConflictAction.MergedReplaced"/> per overwritten file under
    /// <see cref="RestoreDirectoryConflict.MergeReplace"/>, and one
    /// <see cref="RecycleRestoreConflictAction.MergedRenamed"/> per renamed file under
    /// <see cref="RestoreDirectoryConflict.MergeRename"/>. A tree that collides with nothing reports nothing,
    /// which is the same answer any policy gives when there was in fact nothing in the way.
    /// <para>
    /// Two policies report nothing here. <see cref="RestoreFileConflict.Replace"/> resolves its own conflict by
    /// taking the occupant's place, and a <c>Merge*</c> value whose destination holds a <em>file</em> has no tree
    /// to merge into — the latter fails at <see cref="RecycleFailureStage.RestoreFailed"/> with the entry left
    /// whole in the bin.
    /// </para>
    /// <para>
    /// None of that makes <see cref="Succeeded"/> false: a conflict here is an outcome the caller asked about by
    /// choosing a policy, and <see cref="Succeeded"/> is decided by <see cref="Failures"/> alone. Which is a
    /// statement about the policy and not about the outcome — a restore that has reported here can still read
    /// <see langword="false"/>, and a merge that reported what it settled and then could not deal with one file
    /// of its tree carries records in both lists at once.
    /// </para>
    /// </remarks>
    public IReadOnlyList<RecycleRestoreConflict> Conflicts { get; }

    /// <summary>
    /// Items that could not be restored: the payload would not move, its record would not go, or a conflict
    /// was refused under <see cref="RestoreFileConflict.Error"/> or
    /// <see cref="RestoreDirectoryConflict.Error"/>.
    /// </summary>
    /// <remarks>
    /// Every item here is still in the Recycle Bin, which is what makes a second attempt worth making —
    /// except where the payload had already moved and only its record was left, and then the file is already
    /// on disk and the next attempt finds a conflict instead.
    /// <para>
    /// A merge reports a refusal of a file of its tree under that file's own path rather than the directory's,
    /// alongside the directory's own outcome. Such a file is put back into the Recycle Bin with a record of its
    /// own; where even that could not be done the payload is left whole and the directory's entry is still in
    /// the bin with it inside.
    /// </para>
    /// <para>
    /// One file can account for <b>two</b> records under its own path, and the directory a third under its own,
    /// each a separate refusal with its own code. Group by path for one failure per file; read the list as
    /// events and it is what it says it is.
    /// </para>
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
    /// <see langword="true"/> when nothing failed.
    /// </summary>
    /// <remarks>
    /// Failures decide it, and conflicts do not: an item left in the Recycle Bin by
    /// <see cref="RestoreFileConflict.Skip"/> is an answer the caller asked for, not a failure to report
    /// it as one. So a skipped item does not make this <see langword="false"/> — it is one answer among the
    /// others, and another entry's failure still is.
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
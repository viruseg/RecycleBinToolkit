namespace RecycleBinToolkit;

/// <summary>
/// What to do with a path the Recycle Bin's index record cannot name.
/// </summary>
/// <remarks>
/// A version 1 record holds a path in a fixed 520-byte field, so a path past 259 characters cannot be
/// described at all. It applies on a machine old enough to be written that way and nowhere else: a version
/// 2 record carries a length, and a path Windows can name fits.
/// </remarks>
public enum LongPathFailureAction
{
    /// <summary>
    /// Leave the file untouched and report it at <see cref="RecycleFailureStage.LongPathNotRecyclable"/>.
    /// </summary>
    /// <remarks>
    /// The default, and the only choice that cannot lose data. It raises <see cref="PathTooLongException"/>
    /// on the single-path overload only when <see cref="RecycleBinOptions.ThrowOnError"/> is also on: the
    /// global switch has the last say, so a caller who turned throwing off gets the failure in
    /// <see cref="RecycleBinResult.Failures"/> instead of an exception.
    /// </remarks>
    Throw,

    /// <summary>
    /// Delete the file for good. The caller asked for this, and the path is reported in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> so the outcome is never silent.
    /// </summary>
    /// <remarks>
    /// None of this applies to a path on a volume that cannot recycle it under the two actions that settle
    /// the volume first. <see cref="NoRecycleBinAction.Throw"/> refuses such a path at
    /// <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/> with the file left in place, and
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> has the volume gate remove it outright, so it
    /// arrives in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> from there rather than from here.
    /// Under <see cref="NoRecycleBinAction.AllowIndeterminate"/> the path is handed over, and this action
    /// is what then decides its fate. See <see cref="NoRecycleBinAction"/>.
    /// </remarks>
    DeletePermanently,

    /// <summary>
    /// Move the item to a short path on its own volume and recycle it from there.
    /// </summary>
    /// <remarks>
    /// The move is a rename, so it costs nothing whatever the file size, and the item lands in the Recycle
    /// Bin of the volume it came from. It is also the only way to get a file past a length limit into the
    /// Recycle Bin at all, which is what makes it the last answer rather than the first.
    /// <para>
    /// What the caller gives up is the original location in the Recycle Bin: the entry shows the staging
    /// path the item was deleted from, not where it lived. If the recycle then fails, the item is moved
    /// back. An entry still sitting in the bin when it is restored goes to its staging path, which the
    /// operation has since removed — the alternative was refusing the item at all.
    /// </para>
    /// </remarks>
    ShortenPathAndRecycle
}
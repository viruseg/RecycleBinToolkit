namespace RecycleBinToolkit;

/// <summary>
/// What to do with a path the Shell is able to delete but not able to recycle.
/// </summary>
/// <remarks>
/// The Shell honours <c>MAX_PATH</c> when it recycles: past it, the item is removed permanently while
/// the operation still reports success and no recycle location. Rather than lose the file silently,
/// such a path is either refused with the file left in place, or removed for good when the caller has
/// explicitly asked for that.
/// </remarks>
public enum LongPathFailureAction
{
    /// <summary>
    /// Leave the file untouched and report a <see cref="RecycleFailureStage.LongPathNotRecyclable"/>
    /// failure.
    /// </summary>
    /// <remarks>
    /// This is the default, and the only choice that cannot lose data. Note that it raises
    /// <see cref="PathTooLongException"/> only when <see cref="RecycleBinOptions.ThrowOnError"/> is
    /// also on: the global switch has the final say, so a caller that turned throwing off gets the
    /// failure in <see cref="RecycleBinResult.Failures"/> instead of an exception.
    /// </remarks>
    Throw,

    /// <summary>
    /// Delete the file permanently. The caller asked for this, and the path is reported in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> so the outcome is never silent.
    /// </summary>
    DeletePermanently,

    /// <summary>
    /// Move the item to a short path on the same volume and recycle it from there, which is the only
    /// way to get a file past the Shell's limit into the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// The move is a rename, so it costs nothing regardless of file size. What the caller gives up is
    /// the original location in the Recycle Bin: the entry shows the staging path the item was deleted
    /// from, not where it lived. If the recycle then fails, the item is moved back.
    /// </remarks>
    ShortenPathAndRecycle
}
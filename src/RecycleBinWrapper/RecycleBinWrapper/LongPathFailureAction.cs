namespace Utils;

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
    /// failure. The default, and the only choice that cannot lose data.
    /// </summary>
    Throw,

    /// <summary>
    /// Delete the file permanently. The caller asked for this, and the path is reported in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> so the outcome is never silent.
    /// </summary>
    DeletePermanently
}
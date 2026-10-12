namespace RecycleBinToolkit;

/// <summary>What to do with a path on a volume that cannot recycle it, safest first.</summary>
public enum NoRecycleBinAction
{
    /// <summary>
    /// Refuse the path and leave the file where it is. The default, and the only value that cannot lose
    /// data. Raises only when <see cref="RecycleOptions.ThrowOnError"/> is on.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Recycle the path anyway, which creates a Recycle Bin on a volume that had none. It comes back in
    /// <see cref="RecycleBinResult.RecycledPaths"/> and never in
    /// <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// </summary>
    AllowIndeterminate = 1,

    /// <summary>
    /// Remove the path for good, reported in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/> and
    /// never in <see cref="RecycleBinResult.RecycledPaths"/>. A file system that refuses reports
    /// <see cref="RecycleFailureStage.PermanentDeleteFailed"/> and leaves the file where it was.
    /// </summary>
    DeletePermanently = 2
}
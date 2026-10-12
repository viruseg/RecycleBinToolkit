namespace RecycleBinToolkit;

/// <summary>What to do with a path on a volume that cannot recycle it.</summary>
public enum NoRecycleBinAction
{
    /// <summary>
    /// Refuse the path and leave the file untouched. The default.
    /// <see cref="RecycleBin.RecycleAsync(string?, RecycleOptions?, CancellationToken)"/> raises for it only
    /// when <see cref="RecycleOptions.ThrowOnError"/> is on; the batch overload reports it in
    /// <see cref="RecycleBinResult.Failures"/>.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Recycle the path anyway, which creates a Recycle Bin on a volume that had none. The path comes back in
    /// <see cref="RecycleBinResult.RecycledPaths"/>.
    /// </summary>
    AllowIndeterminate = 1,

    /// <summary>
    /// Delete the path permanently. It is reported in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
    /// A file system that refuses reports <see cref="RecycleFailureStage.PermanentDeleteFailed"/> and leaves
    /// the file untouched.
    /// </summary>
    DeletePermanently = 2
}
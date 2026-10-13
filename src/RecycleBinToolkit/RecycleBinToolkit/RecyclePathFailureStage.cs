namespace RecycleBinToolkit;

/// <summary>Where a <see cref="RecycleBin.RecycleAsync(IReadOnlyList{string?}, RecycleOptions?, CancellationToken)"/> call stopped for one path.</summary>
public enum RecyclePathFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation,

    /// <summary>The move to the Recycle Bin failed for this item.</summary>
    Performing,

    /// <summary>
    /// The volume cannot recycle the path and the caller asked for a Recycle Bin. Nothing was attempted:
    /// <see cref="RecycleFailure{TStage}.Attempts"/> is <c>0</c> and the file is untouched.
    /// </summary>
    NoRecycleBinOnVolume,

    /// <summary>
    /// The path names its volume by GUID, as <c>\\?\Volume{GUID}\…</c>, which has no drive letter to be
    /// measured or reported against. Nothing was attempted: <see cref="RecycleFailure{TStage}.Attempts"/> is
    /// <c>0</c>.
    /// </summary>
    VolumeGuidPathNotAddressable,

    /// <summary>
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> was asked for and the file system refused. The file
    /// is untouched.
    /// </summary>
    PermanentDeleteFailed,

    /// <summary>
    /// A path the Recycle Bin's index record cannot name, offered and refused. Nothing was moved:
    /// <see cref="RecycleFailure{TStage}.Attempts"/> is <c>0</c> and the file is untouched.
    /// </summary>
    /// <remarks>
    /// Reachable only where the record in use has a fixed-size path field. Where
    /// <see cref="LongPathFailureAction.DeletePermanently"/> was set the path is removed instead, and a
    /// refusal from the file system under it arrives as <see cref="PermanentDeleteFailed"/> rather than
    /// blamed on the length.
    /// </remarks>
    LongPathNotRecyclable
}
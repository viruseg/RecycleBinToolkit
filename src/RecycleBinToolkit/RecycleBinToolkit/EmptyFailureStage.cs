namespace RecycleBinToolkit;

/// <summary>Where an <see cref="RecycleBin.EmptyAsync(string?, EmptyOptions?, CancellationToken)"/> call stopped for one volume.</summary>
public enum EmptyFailureStage
{
    /// <summary>
    /// Nothing usable came back from the volume's Recycle Bin: either it could not be walked at all, or one
    /// record on it could not be read. Nothing is claimed about what is left on the volume, and it is not
    /// named in <see cref="RecycleBinEmptyResult.VolumesEmptied"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="RecycleFailure{TStage}.Path"/> names the volume root that could not be walked, or the
    /// record inside the Recycle Bin that could not be read.
    /// </remarks>
    Enumeration,

    /// <summary>
    /// An entry on the volume could not be removed, because something was holding it open or this account may
    /// not remove it. It is still in the Recycle Bin, and its size is not counted in
    /// <see cref="RecycleBinEmptyResult.BytesFreed"/>.
    /// </summary>
    /// <remarks>
    /// The rest of the volume was emptied regardless, and the volume is not named in
    /// <see cref="RecycleBinEmptyResult.VolumesEmptied"/> because something was left behind on it.
    /// <see cref="RecycleFailure{TStage}.Path"/> names the entry.
    /// </remarks>
    EntryNotRemoved
}
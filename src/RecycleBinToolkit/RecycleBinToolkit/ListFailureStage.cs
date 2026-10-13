namespace RecycleBinToolkit;

/// <summary>Where a <see cref="RecycleBin.GetItemsAsync(string?, GetItemsOptions?, CancellationToken)"/> call stopped for one volume.</summary>
public enum ListFailureStage
{
    /// <summary>
    /// Nothing usable came back from the volume's Recycle Bin: either the volume could not be walked at all,
    /// or one record on it could not be read and every other entry was.
    /// </summary>
    /// <remarks>
    /// <see cref="RecycleFailure{TStage}.Path"/> names the volume root where the walk could not start, or
    /// the record inside the Recycle Bin that could not be read — never the path the deleted item was
    /// originally at.
    /// </remarks>
    Enumeration
}
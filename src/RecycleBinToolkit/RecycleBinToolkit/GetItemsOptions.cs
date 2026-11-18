namespace RecycleBinToolkit;

/// <summary>Settings for <see cref="RecycleBin.GetItemsAsync(string?, GetItemsOptions?, CancellationToken)"/>.</summary>
/// <remarks>
/// <see langword="null"/> means <see cref="Default"/>.
/// </remarks>
public sealed class GetItemsOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static GetItemsOptions Default { get; } = new();

    /// <summary>
    /// Whether the listing reads every entry's record up front. Defaults to <c>false</c>, so a listing pays
    /// nothing for details nobody asks for.
    /// </summary>
    /// <remarks>
    /// Turned on, an entry whose record cannot be read is reported in <see cref="RecycleBinContents.Failures"/>
    /// rather than being returned and raising later.
    /// </remarks>
    public new bool PreloadItemDetails
    {
        get => base.PreloadItemDetails;
        init => base.PreloadItemDetails = value;
    }

    /// <summary>
    /// Whether the listing measures what lies beneath each recycled directory and reports it as that entry's
    /// <see cref="RecycleBinItem.Size"/>. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Turned off, a directory's <see cref="RecycleBinItem.Size"/> is <c>0</c>, which is the figure its record
    /// carries. Turned on, the listing walks the files beneath every directory entry, at every depth.
    /// </remarks>
    public new bool MeasureDirectorySizes
    {
        get => base.MeasureDirectorySizes;
        init => base.MeasureDirectorySizes = value;
    }
}
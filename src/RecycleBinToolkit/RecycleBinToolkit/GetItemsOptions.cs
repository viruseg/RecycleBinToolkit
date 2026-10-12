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
}
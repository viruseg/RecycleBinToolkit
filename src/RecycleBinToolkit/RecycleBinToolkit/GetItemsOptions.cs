namespace RecycleBinToolkit;

/// <summary>
/// Settings for <see cref="RecycleBin.GetItemsAsync(string?, RecycleBinOptions?, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <see cref="Default"/> is a valid argument, and <see langword="null"/> means the same thing.
/// </remarks>
public sealed class GetItemsOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static GetItemsOptions Default { get; } = new();

    /// <summary>
    /// Whether a listing reads every entry's record up front instead of leaving each entry to read its own
    /// on first use. Defaults to <c>false</c>, so a listing pays nothing for details nobody asks for.
    /// </summary>
    /// <remarks>
    /// Turned on, the reads happen during the listing rather than one at a time from the caller's own loop,
    /// and an entry whose record cannot be read is reported in <see cref="RecycleBinContents.Failures"/>
    /// rather than being returned and raising later. A caller that reads every item's path pays the same
    /// either way, only spread differently.
    /// <para>
    /// It says nothing about <see cref="RecycleBin.EmptyAsync"/>, which does not read records for entries
    /// whose payload is present.
    /// </para>
    /// </remarks>
    public new bool PreloadItemDetails
    {
        get => base.PreloadItemDetails;
        init => base.PreloadItemDetails = value;
    }
}
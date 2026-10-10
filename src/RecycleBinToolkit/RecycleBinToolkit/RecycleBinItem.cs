namespace RecycleBinToolkit;

/// <summary>
/// One item currently sitting in the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// <see cref="Size"/> and <see cref="IsDirectory"/> are known the moment the entry is enumerated.
/// <see cref="OriginalPath"/> and <see cref="DeletedAt"/> are not: on the native backend they live in the
/// entry's <c>$I</c> record, and reading every one of those is the whole cost of a listing. The first
/// access to either field reads that record once and answers both.
/// <para>
/// On the Shell backend every field is already known when the listing is built, so nothing here is ever
/// read after the fact.
/// </para>
/// </remarks>
public sealed class RecycleBinItem : IEquatable<RecycleBinItem>
{
    /// <summary>The two fields that come out of one <c>$I</c> record, read together or not at all.</summary>
    private readonly record struct RecordDetails(string OriginalPath, DateTimeOffset DeletedAt);

    private readonly string? _indexPath;
    private readonly Lazy<RecordDetails>? _details;
    private readonly string? _originalPath;
    private readonly DateTimeOffset _deletedAt;

    /// <summary>
    /// Creates an entry whose record is read on demand.
    /// </summary>
    /// <param name="indexPath">Path of the <c>$I</c> record describing the item. Read on first use of the two lazy fields.</param>
    /// <param name="payloadPath">Physical <c>$R</c> path of the payload.</param>
    /// <param name="size">Size in bytes, or <c>0</c> for a directory.</param>
    /// <param name="isDirectory">Whether the item is a directory.</param>
    internal RecycleBinItem(string indexPath, string payloadPath, long size, bool isDirectory)
    {
        _indexPath = indexPath;
        PayloadPath = payloadPath;
        Size = size;
        IsDirectory = isDirectory;

        _details = new Lazy<RecordDetails>(
            () => Read(indexPath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Creates an entry whose details are already known, so no property access reads anything.
    /// </summary>
    /// <param name="indexPath">Path of the <c>$I</c> record, or <see langword="null"/> where the backend keeps no record for the item.</param>
    /// <param name="payloadPath">Physical <c>$R</c> path of the payload.</param>
    /// <param name="size">Size in bytes, or <c>0</c> for a directory.</param>
    /// <param name="isDirectory">Whether the item is a directory.</param>
    /// <param name="originalPath">Full path the item was deleted from.</param>
    /// <param name="deletedAt">When the item was deleted, in the local offset.</param>
    internal RecycleBinItem(
        string? indexPath,
        string payloadPath,
        long size,
        bool isDirectory,
        string originalPath,
        DateTimeOffset deletedAt)
        : this(indexPath, payloadPath, size, isDirectory, new RecordDetails(originalPath, deletedAt))
    {
    }

    private RecycleBinItem(
        string? indexPath,
        string payloadPath,
        long size,
        bool isDirectory,
        RecordDetails details)
    {
        _indexPath = indexPath;
        PayloadPath = payloadPath;
        Size = size;
        IsDirectory = isDirectory;
        _originalPath = details.OriginalPath;
        _deletedAt = details.DeletedAt;
    }

    /// <summary>Size in bytes. <c>0</c> for a directory, whose contents are not measured.</summary>
    public long Size { get; }

    /// <summary>Whether the item is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Physical <c>$R</c> path of the payload, inside the Recycle Bin.</summary>
    internal string PayloadPath { get; }

    /// <summary>
    /// Full path the item was deleted from.
    /// </summary>
    /// <exception cref="IOException">
    /// The item's <c>$I</c> record could not be read — it was removed or locked between the listing and this
    /// access. The same exception is raised by every later access, because the attempt is not repeated.
    /// </exception>
    public string OriginalPath => Details.OriginalPath;

    /// <summary>
    /// When the item was deleted, in the local offset.
    /// </summary>
    /// <exception cref="IOException">
    /// The item's <c>$I</c> record could not be read. See <see cref="OriginalPath"/>.
    /// </exception>
    public DateTimeOffset DeletedAt => Details.DeletedAt;

    /// <summary>
    /// Creates an item whose details are already read, or reports that they could not be.
    /// </summary>
    /// <param name="indexPath">Path of the <c>$I</c> record describing the item.</param>
    /// <param name="payloadPath">Physical <c>$R</c> path of the payload.</param>
    /// <param name="size">Size in bytes, or <c>0</c> for a directory.</param>
    /// <param name="isDirectory">Whether the item is a directory.</param>
    /// <param name="hResult">
    /// Receives the code the filesystem raised, or <c>0</c> when the file was readable and simply was not a
    /// record — the same distinction <see cref="NativeIndexFile.TryRead"/> draws, and for the same reason.
    /// </param>
    /// <returns>
    /// An item that answers both details without reading anything, or <see langword="null"/> when there is
    /// none to answer them from.
    /// </returns>
    /// <remarks>
    /// This is what <see cref="RecycleBinOptions.PreloadItemDetails"/> uses, and it exists so that the two
    /// ways an item gets its details read a record through one path and answer it the same way.
    /// </remarks>
    internal static RecycleBinItem? Warmed(
        string indexPath,
        string payloadPath,
        long size,
        bool isDirectory,
        out int hResult)
    {
        if (!NativeIndexFile.TryRead(indexPath, out var record, out hResult))
        {
            return null;
        }

        return new RecycleBinItem(indexPath, payloadPath, size, isDirectory, ToDetails(record));
    }

    private RecordDetails Details => _details is not null
        ? _details.Value
        : new RecordDetails(_originalPath!, _deletedAt);

    /// <summary>
    /// Reads one record and answers both fields from it.
    /// </summary>
    private static RecordDetails Read(string indexPath)
    {
        if (!NativeIndexFile.TryRead(indexPath, out var record, out var hResult))
        {
            throw new IOException(
                $"The Recycle Bin record for an item was read when one of its details was asked for, and it " +
                $"could not be read: '{indexPath}' (0x{hResult:X8}). It was removed or locked between the " +
                "listing and this access. Listing the Recycle Bin again reports what is actually there.");
        }

        return ToDetails(record);
    }

    /// <summary>
    /// Turns what a record says into the two fields it is read for.
    /// </summary>
    /// <remarks>
    /// The deletion time is converted here rather than per access, because the conversion is the only work
    /// between the record and a value a caller reads, and repeating it on every read would be the property's
    /// whole cost.
    /// </remarks>
    private static RecordDetails ToDetails(NativeIndexRecord record) =>
        new(record.OriginalPath, new DateTimeOffset(record.DeletedAtUtc.ToLocalTime()));

    /// <summary>
    /// Two entries are the same when they are the same stored entry. Compared on the path that identifies it,
    /// without reading either one's details.
    /// </summary>
    /// <param name="other">The entry to compare with.</param>
    /// <returns><see langword="true"/> when both name the same entry in the Recycle Bin.</returns>
    public bool Equals(RecycleBinItem? other) =>
        other is not null &&
        string.Equals(Identity, other.Identity, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RecycleBinItem);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Identity);

    /// <summary>
    /// What names this entry among all entries: its <c>$I</c> record where there is one, and its payload
    /// otherwise. Both are unique per entry, and neither requires reading the record.
    /// </summary>
    private string Identity => _indexPath ?? PayloadPath;
}

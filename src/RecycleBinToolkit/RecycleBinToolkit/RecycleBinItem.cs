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

    /// <summary>
    /// The record still to be read, or <see langword="null"/> once there is nothing left to read.
    /// </summary>
    /// <remarks>
    /// Written by <see cref="LoadDetails"/> through <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>
    /// rather than by the constructors alone, so it is the one field in the type that is not readonly. It is a
    /// single reference either way — never half of an item, and never a torn pair of values — and
    /// <see cref="_resolved"/> beside it stays readonly so the two cannot be seen disagreeing.
    /// </remarks>
    private Lazy<RecordDetails>? _details;

    private readonly RecordDetails _resolved;

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
        _details = null;
        _resolved = details;
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
    /// record — the same distinction the record reader draws, and for the same reason.
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

    private RecordDetails Details => _details is null ? _resolved : _details.Value;

    /// <summary>
    /// Whether this entry's details are already in memory, whether they were read when it was built or since.
    /// </summary>
    /// <remarks>
    /// Reading it is a field test and touches no disk, which is what makes it safe to ask every entry of a
    /// listing: a batch that asks ninety thousand of them pays ninety thousand field reads, not ninety thousand
    /// file reads.
    /// </remarks>
    internal bool IsDetailsLoaded => _details is null || _details.IsValueCreated;

    /// <summary>
    /// The volume this entry's record lives on, or <see langword="null"/> where there is no record to live on.
    /// </summary>
    /// <remarks>
    /// A batch that loads details reads it once per volume, because two volumes can sit on different hardware
    /// and one degree of parallelism would be wrong for one of them.
    /// </remarks>
    internal string? VolumeRoot => _indexPath is null ? null : Path.GetPathRoot(_indexPath);

    /// <summary>
    /// Reads this entry's record now rather than on first access.
    /// </summary>
    /// <param name="hResult">
    /// Receives the code the filesystem raised, or <c>0</c> when the file was readable and simply was not a
    /// record. Meaningless when this method returns <see langword="true"/> for an entry with nothing to read.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when there is nothing left to read — this entry is now loaded. Otherwise whether
    /// this call is what read it.
    /// </returns>
    /// <remarks>
    /// A record that cannot be read leaves the entry exactly as it was: cold, and raising the same
    /// <see cref="IOException"/> it would have raised anyway at the moment a caller asks for its path. Failing
    /// here instead would turn one dead entry into a failed batch, and half-loading it would answer one field
    /// from nowhere.
    /// <para>
    /// The swap is a single reference exchange rather than a field write, so a caller reading the same entry
    /// from another thread sees either the old record source or the new resolved one and never a mixture.
    /// Two callers racing here may both read the file; that costs one read and changes nothing, which is
    /// cheaper than the lock that would stop it.
    /// </para>
    /// </remarks>
    internal bool LoadDetails(out int hResult)
    {
        var pending = _details;

        if (pending is null || pending.IsValueCreated)
        {
            hResult = 0;
            return true;
        }

        if (!NativeIndexFile.TryRead(_indexPath!, out var record, out hResult))
        {
            return false;
        }

        // Built already evaluated and over a value rather than a factory, so replacing the source of the read
        // with the answer cannot leave anything to read on the way out.
        Interlocked.CompareExchange(ref _details, new Lazy<RecordDetails>(ToDetails(record)), pending);
        return true;
    }

    /// <summary>
    /// Reads one record and answers both fields from it.
    /// </summary>
    private static RecordDetails Read(string indexPath)
    {
        if (!NativeIndexFile.TryRead(indexPath, out var record, out var hResult))
        {
            // Two different things end here and the caller acts on them differently, so the message says which.
            // A zero code is a file that opened and turned out not to be a record — a truncated write, or
            // something in the bin that never was one. Blaming a removal for that would send a caller looking
            // for a race against another process that never happened.
            var because = hResult == 0
                ? "the file is readable but is not a Recycle Bin record — it is truncated, or it was never one"
                : $"it could not be opened (0x{hResult:X8}), so it was removed or locked since the listing";

            throw new IOException(
                $"The Recycle Bin record for an item was read when one of its details was asked for, and " +
                $"nothing could be read from '{indexPath}': {because}. Listing the Recycle Bin again reports " +
                "what is actually there.",
                hResult);
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

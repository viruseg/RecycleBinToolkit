namespace RecycleBinToolkit;

/// <summary>
/// One item currently sitting in the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// <see cref="Size"/> and <see cref="IsDirectory"/> are answered from the listing.
/// <see cref="OriginalPath"/> and <see cref="DeletedAt"/> are read from the item's own record on first access
/// to either, which answers both at once and can raise <see cref="IOException"/>.
/// </remarks>
public sealed class RecycleBinItem : IEquatable<RecycleBinItem>
{
    /// <summary>The two fields that come out of one <c>$I</c> record, read together or not at all.</summary>
    private readonly record struct RecordDetails(string OriginalPath, DateTimeOffset DeletedAt);

    private readonly string _indexPath;

    /// <summary>
    /// The record still to be read, or <see langword="null"/> once there is nothing left to read.
    /// </summary>
    /// <remarks>
    /// The one field whose value changes after construction, and only in one direction: from a record source to
    /// nothing. Read with <see cref="Volatile.Read{T}"/> and cleared with an
    /// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>, which is what pairs the write to
    /// <see cref="_resolved"/> with the read that observes its absence.
    /// </remarks>
    private Lazy<RecordDetails>? _details;

    /// <summary>
    /// What a loaded entry answers from, valid exactly when <see cref="_details"/> is
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Not readonly because <see cref="LoadDetails"/> fills it in place. That is what keeps a warmed entry to
    /// the two field writes it costs a pre-warmed one: the earlier shape swapped in a fresh
    /// <see cref="Lazy{T}"/>, which for a struct is an object plus a box of it, retained for the life of the
    /// entry — on a bin of eighteen thousand, thirty-six thousand objects a caller cannot see or reclaim.
    /// </remarks>
    private RecordDetails _resolved;

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
    /// <param name="indexPath">Path of the <c>$I</c> record describing the item.</param>
    /// <param name="payloadPath">Physical <c>$R</c> path of the payload.</param>
    /// <param name="size">Size in bytes, or <c>0</c> for a directory.</param>
    /// <param name="isDirectory">Whether the item is a directory.</param>
    /// <param name="originalPath">Full path the item was deleted from.</param>
    /// <param name="deletedAt">When the item was deleted, in the local offset.</param>
    internal RecycleBinItem(
        string indexPath,
        string payloadPath,
        long size,
        bool isDirectory,
        string originalPath,
        DateTimeOffset deletedAt)
        : this(indexPath, payloadPath, size, isDirectory, new RecordDetails(originalPath, deletedAt))
    {
    }

    private RecycleBinItem(
        string indexPath,
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

    /// <summary>Physical <c>$I</c> path of the record describing the item, inside the Recycle Bin.</summary>
    /// <remarks>
    /// Read without touching disk, like <see cref="PayloadPath"/> and unlike
    /// <see cref="OriginalPath"/>, which this record is what holds. A restore deletes it once the payload has
    /// been put back.
    /// </remarks>
    internal string IndexPath => _indexPath;

    /// <summary>Full path the item was deleted from.</summary>
    /// <exception cref="IOException">
    /// The item's record could not be read — it was removed or locked between the listing and this access.
    /// The same exception is raised by every later access, because the attempt is not repeated.
    /// </exception>
    public string OriginalPath => Details.OriginalPath;

    /// <summary>When the item was deleted, in the local offset.</summary>
    /// <exception cref="IOException">
    /// The item's record could not be read. See <see cref="OriginalPath"/>.
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
    /// This is what <see cref="GetItemsOptions.PreloadItemDetails"/> uses, and it exists so that the two
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

    /// <summary>
    /// The two fields, from the record still to be read or from what a load already resolved.
    /// </summary>
    /// <remarks>
    /// The <see cref="Volatile.Read{T}"/> is the acquire half of the pair <see cref="LoadDetails"/> publishes
    /// with: an entry that sees no record source also sees the values written before the source was taken away.
    /// A plain read would not promise that, and a torn read here is a path from one entry and a time from
    /// another.
    /// </remarks>
    private RecordDetails Details
    {
        get
        {
            var pending = Volatile.Read(ref _details);
            return pending is null ? _resolved : pending.Value;
        }
    }

    /// <summary>
    /// Whether this entry's details are already in memory, whether they were read when it was built or since.
    /// </summary>
    /// <remarks>
    /// Reading it is a field test and touches no disk, which is what makes it safe to ask every entry of a
    /// listing: a batch that asks ninety thousand of them pays ninety thousand field reads, not ninety thousand
    /// file reads.
    /// </remarks>
    internal bool IsDetailsLoaded => Volatile.Read(ref _details) is null || _details!.IsValueCreated;

    /// <summary>
    /// The volume this entry's record lives on, without allocating.
    /// </summary>
    /// <remarks>
    /// A batch that loads details asks every pending entry for this once, so a property that allocated would
    /// leave eighteen thousand identical three-character strings behind per call on the bin this exists for. The
    /// span is only materialised when a volume is seen for the first time.
    /// <para>
    /// A share answers <c>\\server\share</c> without a trailing separator. That is a different string from the
    /// normalised root the listing itself uses, so the two miss each other in
    /// <see cref="VolumeSeekPenalty"/>'s cache and it stores both. Nothing changes for the answer, which is one
    /// thread for a share either way.
    /// </para>
    /// </remarks>
    internal ReadOnlySpan<char> VolumeRoot => Path.GetPathRoot(_indexPath.AsSpan());

    /// <summary>
    /// Reads this entry's record now rather than on first access.
    /// </summary>
    /// <param name="hResult">
    /// Receives the code the filesystem raised, or <c>0</c> when the record was readable, or when it was
    /// readable and simply was not a record — the same distinction the record reader draws, for the same
    /// reason. Meaningless when this method returns <see langword="true"/> for an entry with nothing to read.
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
        var pending = Volatile.Read(ref _details);

        if (pending is null || pending.IsValueCreated)
        {
            hResult = 0;
            return true;
        }

        if (!NativeIndexFile.TryRead(_indexPath, out var record, out hResult))
        {
            return false;
        }

        // Written before the source is taken away, and the exchange is what makes the order observable: a
        // reader that acquires a null out of `_details` acquires the values above with it.
        _resolved = ToDetails(record);
        Interlocked.CompareExchange(ref _details, null, pending);
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
    /// What names this entry among all entries: its <c>$I</c> record, unique per entry, without reading it.
    /// </summary>
    private string Identity => _indexPath;
}

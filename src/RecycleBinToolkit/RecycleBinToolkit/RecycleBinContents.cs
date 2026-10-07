namespace RecycleBinToolkit;

/// <summary>
/// The Recycle Bin contents returned by <see cref="RecycleBin.GetItemsAsync"/>.
/// </summary>
/// <param name="Items">The items that were read.</param>
/// <param name="Failures">Items or volumes that could not be read.</param>
/// <param name="Elapsed">Wall-clock time spent enumerating.</param>
public sealed class RecycleBinContents
{
    /// <summary>Creates a listing.</summary>
    /// <param name="items">The items that were read.</param>
    /// <param name="failures">Items or volumes that could not be read.</param>
    /// <param name="elapsed">Wall-clock time spent enumerating.</param>
    public RecycleBinContents(
        IReadOnlyList<RecycleBinItem> items,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed)
    {
        Items = items;
        Failures = failures;
        Elapsed = elapsed;
    }

    /// <summary>The items that were read.</summary>
    public IReadOnlyList<RecycleBinItem> Items { get; }

    /// <summary>Items or volumes that could not be read.</summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Wall-clock time spent enumerating.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Sum of <see cref="RecycleBinItem.Size"/> over <see cref="Items"/>.</summary>
    public long TotalSize { get; init; }
}

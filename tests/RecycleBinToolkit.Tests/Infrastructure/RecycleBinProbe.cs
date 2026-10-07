namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Reads a volume's Recycle Bin occupancy through the library's own listing API, so the delta
/// assertions across the suites are stated against what a caller would see.
/// </summary>
/// <remarks>
/// The count and the size come from one enumeration, and the <c>-1</c> sentinel keeps its meaning: a
/// volume that cannot be read reports failure rather than an empty Recycle Bin, because the two are
/// indistinguishable once they are both zero.
/// <para>
/// <see cref="RecycleBinVolume"/> remains the independent oracle, and
/// <see cref="Suites.ShellSuite.RecycleBinListingAgreesWithTheShellOracle"/> is what pins this probe to
/// it. Both live in one place on purpose: an assertion that checks the library against itself proves
/// nothing, so the agreement is asserted once and the rest of the suites build on it.
/// </para>
/// </remarks>
internal static class RecycleBinProbe
{
    /// <summary>
    /// Number of entries the given volume's Recycle Bin holds.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"E:\\"</c>.</param>
    /// <returns>The entry count, or <c>-1</c> when the volume could not be read.</returns>
    public static long QueryItemCount(string volumeRoot) => Measure(volumeRoot).Items;

    /// <summary>
    /// Total size of the entries the given volume's Recycle Bin holds.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"E:\\"</c>.</param>
    /// <returns>The size in bytes, or <c>-1</c> when the volume could not be read.</returns>
    public static long QuerySize(string volumeRoot) => Measure(volumeRoot).Bytes;

    /// <summary>
    /// Both quantities from a single enumeration, for assertions that need them together.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"E:\\"</c>.</param>
    /// <returns>The entry count and the size in bytes, each <c>-1</c> when the volume could not be read.</returns>
    public static (long Items, long Bytes) Measure(string volumeRoot)
    {
        var contents = RecycleBin.GetItemsAsync(volumeRoot).ConfigureAwait(false).GetAwaiter().GetResult();

        // A volume that is not mounted filters to nothing and reports no failure, so the fact that it
        // could not be read has to be established from the volume itself rather than from the result.
        if (!Directory.Exists(volumeRoot))
        {
            return (-1, -1);
        }

        return (contents.Items.Count, contents.TotalSize);
    }
}

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Reads a volume's Recycle Bin occupancy through the library's own counting path, so the delta
/// assertions across the suites are stated against what a caller would see.
/// </summary>
/// <remarks>
/// The count and the size come from one query, and the <c>-1</c> sentinel keeps its meaning: a
/// volume that cannot be read reports failure rather than an empty Recycle Bin, because the two are
/// indistinguishable once they are both zero.
/// <para>
/// This asks <c>SHQueryRecycleBin</c>, not the listing. Answering "did the count go up by one" does not
/// need the entries, and a listing costs six COM round trips and a stat per entry: with ninety thousand
/// files in the bin a single listing call took over thirteen minutes, against about eleven seconds for
/// the counter. Seventy times cheaper is not the same as free — the Shell walks the bin's metadata to
/// produce these two numbers, so the cost still grows with how full the bin is and nothing in this
/// library can change that. It does mean the suites are not paying for the part they were not asking
/// for.
/// </para>
/// <para>
/// What this probe no longer covers is the enumeration. It shares <c>SHQueryRecycleBin</c> with
/// <see cref="RecycleBinVolume"/>, so it cannot disagree with the Shell about a count or a size; the one
/// thing it still proves is that the library's own struct is laid out correctly. The listing reaches the
/// Shell by a different route and could disagree with both,
/// <see cref="Suites.ShellSuite.RecycleBinListingAgreesWithTheShellOracle"/> is what checks it, and it
/// reads the listing for that purpose alone.
/// </para>
/// <para>
/// The listing is still covered, and still pinned against this counter:
/// <see cref="Suites.ListerSuite"/> is what exercises it, and
/// <see cref="Suites.ShellSuite.RecycleBinListingAgreesWithTheShellOracle"/> is what asserts the two
/// views agree. <see cref="RecycleBinVolume"/> remains the independent oracle, so an assertion that
/// checks the library against itself proves nothing; the agreement is asserted once and the rest of the
/// suites build on it.
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
    /// Both quantities from a single query, for assertions that need them together.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"E:\\"</c>.</param>
    /// <returns>The entry count and size in bytes, each <c>-1</c> when the volume could not be read.</returns>
    public static (long Items, long Bytes) Measure(string volumeRoot) =>
        RecycleBinCounter.TryQuery(WithSeparator(volumeRoot), out var items, out var bytes)
            ? (items, bytes)
            : (-1, -1);

    /// <summary>
    /// The Shell requires a root with its trailing separator, whatever the caller passed.
    /// </summary>
    private static string WithSeparator(string volumeRoot) =>
        volumeRoot.EndsWith(Path.DirectorySeparatorChar) || volumeRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? volumeRoot
            : volumeRoot + Path.DirectorySeparatorChar;
}
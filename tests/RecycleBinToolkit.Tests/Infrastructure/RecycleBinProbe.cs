using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Reads a volume's Recycle Bin occupancy by walking its bin directory, so the delta
/// assertions across the suites are stated against what a caller would see.
/// </summary>
/// <remarks>
/// The count and the size come from one walk, and the <c>-1</c> sentinel keeps its meaning: a
/// volume that cannot be read reports failure rather than an empty Recycle Bin, because the two are
/// indistinguishable once they are both zero.
/// <para>
/// This walks the bin directory rather than listing the bin. Answering "did the count go up by one"
/// does not need the entries, and a listing reads every record it is asked about afterwards: with
/// ninety thousand files in the bin that is work the count question never needed.
/// </para>
/// <para>
/// What this probe no longer covers is an independent view of the bin. It walks the same directory
/// the library's own listing walks, so it cannot disagree with the library about what is there; the
/// one thing it still proves is that the walk and the record reader agree with the filesystem about
/// counts and sizes. Deltas are what the suites assert, not absolute agreement with anything else.
/// </para>
/// </remarks>
internal static class RecycleBinProbe
{
    /// <summary>
    /// Number of entries the given volume's Recycle Bin holds.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"W:\\"</c>.</param>
    /// <returns>The entry count, or <c>-1</c> when the volume could not be read.</returns>
    public static long QueryItemCount(string volumeRoot) => Measure(volumeRoot).Items;

    /// <summary>
    /// Total size of the entries the given volume's Recycle Bin holds.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"W:\\"</c>.</param>
    /// <returns>The size in bytes, or <c>-1</c> when the volume could not be read.</returns>
    public static long QuerySize(string volumeRoot) => Measure(volumeRoot).Bytes;

    /// <summary>
    /// Both quantities from a single walk, for assertions that need them together.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"W:\\"</c>.</param>
    /// <returns>The entry count and size in bytes, each <c>-1</c> when the volume could not be read.</returns>
    public static (long Items, long Bytes) Measure(string volumeRoot)
    {
        var root = WithSeparator(volumeRoot);

        if (!Directory.Exists(root))
        {
            return (-1, -1);
        }

        var binDirectory = Path.Combine(root, "$RECYCLE.BIN", CurrentSid());

        if (!Directory.Exists(binDirectory))
        {
            return (0, 0);
        }

        try
        {
            long items = 0;
            long bytes = 0;

            foreach (var entry in NativeBinEntries.Collect(binDirectory, CancellationToken.None))
            {
                if (entry.IndexPath is null || entry.PayloadPath is null)
                {
                    continue;
                }

                items++;
                bytes += entry.PayloadIsDirectory ? 0 : entry.PayloadLength;
            }

            return (items, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-1, -1);
        }
    }

    /// <summary>
    /// Both quantities, refusing to answer at all when the volume could not be read.
    /// </summary>
    /// <param name="volumeRoot">Volume root, for example <c>"W:\\"</c>.</param>
    /// <param name="because">What the reading is for, so the failure says which measurement was lost.</param>
    /// <returns>The entry count and size in bytes.</returns>
    /// <remarks>
    /// For the delta assertions, where a baseline is subtracted from a later reading. A failed baseline
    /// arrives as <c>-1</c> and a later reading as some count, and their difference is one larger than
    /// anything that happened — so a bound stated as "gained at least N" passes having measured nothing at
    /// all. The sentinel is right for a query whose result is displayed and wrong for one that is
    /// subtracted, which is the only thing that distinguishes them.
    /// </remarks>
    public static (long Items, long Bytes) MeasureOrFail(string volumeRoot, string because)
    {
        var (items, bytes) = Measure(volumeRoot);

        Check.True(
            items >= 0,
            $"the volume's Recycle Bin was readable for {because} (got count {items}, size {bytes}); a failed " +
            "reading arrives as -1, and a delta taken against one is larger than the truth rather than smaller");

        return (items, bytes);
    }

    private static string CurrentSid() => NativeBinLayout.CurrentSid;

    /// <summary>
    /// The bin directory is addressed from a root with its trailing separator, whatever the caller passed.
    /// </summary>
    private static string WithSeparator(string volumeRoot) =>
        volumeRoot.EndsWith(Path.DirectorySeparatorChar) || volumeRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? volumeRoot
            : volumeRoot + Path.DirectorySeparatorChar;
}

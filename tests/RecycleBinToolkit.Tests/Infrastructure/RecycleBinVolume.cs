using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Independent read of the Recycle Bin of a volume: a second walk of the bin directory that shares
/// nothing with the library's listing but the record reader.
/// </summary>
/// <remarks>
/// Enumerating <c>$Recycle.Bin</c> at the volume root is not an option: the SYSTEM SID subdirectory
/// throws <see cref="UnauthorizedAccessException"/> for a normal user, so only the current user's bin
/// directory is walked. A volume another account's entries sit on is counted without them, the same
/// way the library counts it.
/// </remarks>
internal static class RecycleBinVolume
{
    /// <summary>
    /// Number of items currently tracked in the Recycle Bin of the given volume.
    /// </summary>
    /// <param name="rootPath">Volume root, for example <c>"W:\\"</c>. A missing trailing separator is added.</param>
    /// <returns>The item count, or <c>-1</c> when the query failed.</returns>
    public static long QueryItemCount(string rootPath) =>
        TryQuery(rootPath, out var items, out _, out _) ? items : -1;

    /// <summary>
    /// Total size in bytes of the Recycle Bin contents of the given volume.
    /// </summary>
    /// <param name="rootPath">Volume root, for example <c>"W:\\"</c>. A missing trailing separator is added.</param>
    /// <returns>The size in bytes, or <c>-1</c> when the query failed.</returns>
    public static long QuerySize(string rootPath) =>
        TryQuery(rootPath, out _, out var size, out _) ? size : -1;

    /// <summary>
    /// Both quantities from one walk, with the failure code kept for a readable assertion.
    /// </summary>
    /// <param name="rootPath">Volume root, for example <c>"W:\\"</c>. A missing trailing separator is added.</param>
    /// <returns>
    /// The entry count and size in bytes, both <c>-1</c> when the query failed, and the file system's
    /// error code so a failing assertion can name it.
    /// </returns>
    public static (long Items, long Bytes, int HResult, bool Ok) Measure(string rootPath)
    {
        if (TryQuery(rootPath, out var items, out var size, out var hr))
        {
            return (items, size, 0, true);
        }

        return (-1, -1, hr, false);
    }

    private static bool TryQuery(string rootPath, out long items, out long size, out int hrResult)
    {
        var root = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        items = -1;
        size = -1;
        hrResult = 0;

        if (!Directory.Exists(root))
        {
            return false;
        }

        var binDirectory = Path.Combine(root, "$RECYCLE.BIN", NativeBinLayout.CurrentSid);

        if (!Directory.Exists(binDirectory))
        {
            items = 0;
            size = 0;
            return true;
        }

        try
        {
            items = 0;
            size = 0;

            foreach (var entry in NativeBinEntries.Collect(binDirectory, CancellationToken.None))
            {
                if (entry.IndexPath is null || entry.PayloadPath is null)
                {
                    continue;
                }

                items++;
                size += entry.PayloadIsDirectory ? 0 : entry.PayloadLength;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            hrResult = ex.HResult;
            return false;
        }
    }
}

using System.Runtime.InteropServices;

namespace RecycleBinWrapper.Tests.Infrastructure;

/// <summary>
/// Independent read of the Recycle Bin of a volume. Enumerating <c>$Recycle.Bin</c> directly is
/// not an option: the SYSTEM SID subdirectory throws <see cref="UnauthorizedAccessException"/> for
/// a normal user, so item counts come from the Shell instead.
/// </summary>
internal static partial class RecycleBinVolume
{
    /// <summary>
    /// Number of items currently tracked in the Recycle Bin of the given volume.
    /// </summary>
    /// <param name="rootPath">Volume root, for example <c>"E:\\"</c>. A missing trailing separator is added.</param>
    /// <returns>The item count, or <c>-1</c> when the Shell query failed.</returns>
    public static long QueryItemCount(string rootPath) =>
        TryQuery(rootPath, out var items, out _) ? items : -1;

    /// <summary>
    /// Total size in bytes of the Recycle Bin contents of the given volume.
    /// </summary>
    /// <param name="rootPath">Volume root, for example <c>"E:\\"</c>. A missing trailing separator is added.</param>
    /// <returns>The size in bytes, or <c>-1</c> when the Shell query failed.</returns>
    public static long QuerySize(string rootPath) =>
        TryQuery(rootPath, out _, out var size) ? size : -1;

    private static bool TryQuery(string rootPath, out long items, out long size)
    {
        var root = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        var info = new ShQueryRbInfo { SizeOfSelf = Marshal.SizeOf<ShQueryRbInfo>() };

        var hr = SHQueryRecycleBin(root, ref info);
        if (hr < 0)
        {
            items = -1;
            size = -1;
            return false;
        }

        items = info.NumItems;
        size = info.Size;
        return true;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo info);

    /// <summary>
    /// Native <c>SHQUERYRBINFO</c>.
    /// </summary>
    /// <remarks>
    /// The two 64-bit fields are adjacent and the same width, and the byte total sits before the
    /// item count. Declaration order is therefore spelled out with explicit offsets instead of being
    /// left to the layout rules: swapping the two silently turns every delta assertion into a
    /// comparison of the wrong quantity. <c>ShellSuite.RecycleBinVolumeTracksCountAndSizeSeparately</c>
    /// pins the mapping.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct ShQueryRbInfo
    {
        [FieldOffset(0)]
        public int SizeOfSelf;

        [FieldOffset(8)]
        public long Size;

        [FieldOffset(16)]
        public long NumItems;
    }
}
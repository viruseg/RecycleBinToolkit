using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace RecycleBinWrapper.Tests.Infrastructure;

/// <summary>
/// Independent read of the Recycle Bin of a volume. Enumerating <c>$Recycle.Bin</c> directly is
/// not an option: the SYSTEM SID subdirectory throws <see cref="UnauthorizedAccessException"/> for
/// a normal user, so item counts come from the Shell instead.
/// </summary>
internal static class RecycleBinVolume
{
    /// <summary>
    /// Number of items currently in the Recycle Bin of the given volume.
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
        var info = default(SHQUERYRBINFO);
        info.cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>();

        var root = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        try
        {
            if (PInvoke.SHQueryRecycleBin(root, ref info).Failed)
            {
                items = -1;
                size = -1;
                return false;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            items = -1;
            size = -1;
            return false;
        }

        items = info.i64NumItems;
        size = info.i64Size;
        return true;
    }
}
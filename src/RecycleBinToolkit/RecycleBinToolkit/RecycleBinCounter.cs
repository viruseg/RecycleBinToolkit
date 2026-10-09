using System.Runtime.InteropServices;

namespace RecycleBinToolkit;

/// <summary>
/// Counts what a volume's Recycle Bin holds, straight from the Shell.
/// </summary>
/// <remarks>
/// <c>SHEmptyRecycleBin</c> removes whatever it removes without reporting a single count, so the only
/// way to tell a caller what an empty cost it is to measure the volume on both sides of the call.
/// </remarks>
internal static partial class RecycleBinCounter
{
    /// <summary>
    /// Win32 <c>ERROR_NOT_SET</c>, used when the Shell gives no error code of its own and when a volume
    /// cannot be read at all.
    /// </summary>
    /// <remarks>
    /// Shared rather than declared per caller, so that a volume the caller cannot reach is reported with the
    /// same code whichever backend found it. A caller who switches backends must not see the code change
    /// underneath them.
    /// </remarks>
    internal const int UnreadableVolume = unchecked((int)0x80070490);

    private const int GuidPathCapacity = 64;

    /// <summary>
    /// Reads how many entries a volume's Recycle Bin holds and how much space they take.
    /// </summary>
    /// <param name="volumeRoot">Volume root with a trailing separator, for example <c>"X:\\"</c>.</param>
    /// <param name="items">Entry count, or <c>-1</c> when the Shell query failed.</param>
    /// <param name="bytes">Total size in bytes, or <c>-1</c> when the Shell query failed.</param>
    /// <returns><see langword="true"/> when the query succeeded.</returns>
    internal static bool TryQuery(string volumeRoot, out long items, out long bytes)
    {
        var info = new ShellRecycleBinInfo { SizeOfSelf = Marshal.SizeOf<ShellRecycleBinInfo>() };

        if (SHQueryRecycleBin(volumeRoot, ref info) < 0)
        {
            items = -1;
            bytes = -1;
            return false;
        }

        items = info.ItemCount;
        bytes = info.TotalSize;
        return true;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string rootPath, ref ShellRecycleBinInfo info);

    /// <summary>
    /// Native <c>SHQUERYRBINFO</c>.
    /// </summary>
    /// <remarks>
    /// The two 64-bit fields are adjacent and the same width, so they are placed by explicit offsets
    /// rather than by the layout rules. Letting the compiler lay this out would put the fields
    /// differently per bitness, and the library is AnyCPU: a swapped or misplaced pair would report
    /// sizes as counts, which is exactly the kind of silent wrong answer this assembly exists to
    /// avoid. Offset 8 and 16 assume the documented native packing, in which the 64-bit union is
    /// 8-byte aligned.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    private struct ShellRecycleBinInfo
    {
        [FieldOffset(0)]
        public int SizeOfSelf;

        [FieldOffset(8)]
        public long TotalSize;

        [FieldOffset(16)]
        public long ItemCount;
    }
}

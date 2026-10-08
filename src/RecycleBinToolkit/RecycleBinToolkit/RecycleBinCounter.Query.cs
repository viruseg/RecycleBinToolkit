using System.Runtime.InteropServices;

namespace RecycleBinToolkit;

internal static partial class RecycleBinCounter
{
    /// <summary>
    /// The code the Shell returned for a volume's Recycle Bin query, carried through as it came.
    /// </summary>
    /// <param name="volumeRoot">Volume root with a trailing separator, for example <c>"X:\\"</c>.</param>
    /// <returns>
    /// The Shell's own code: <c>0</c> when the query succeeded, the failure code when it did not. Not
    /// normalised, so the severity bit still means what the Shell meant by it.
    /// </returns>
    /// <remarks>
    /// <see cref="TryQuery(string, out long, out long)"/> answers the same question as a bool and
    /// discards the code, because a count is all a caller of it needs. A refusal is the other kind of
    /// caller: it has to say why, and flattening the code to <c>0</c> would throw away the one fact that
    /// separates a volume the Shell finds no bin on from one it refused for another reason —
    /// <c>0x80070003</c> against <c>0x80004005</c>, both measured on this machine.
    /// <para>
    /// A caller that has just inspected a volume asks again rather than keeping that answer, because the
    /// status carries the reason and not the code. The second query costs one more Shell call on the
    /// refused path only, where the first one failed — and a query that fails has not walked a bin's
    /// metadata, which is the part that is expensive.
    /// </para>
    /// </remarks>
    internal static int QueryHResult(string volumeRoot)
    {
        var info = new ShellRecycleBinInfo { SizeOfSelf = Marshal.SizeOf<ShellRecycleBinInfo>() };

        return SHQueryRecycleBin(volumeRoot, ref info);
    }
}
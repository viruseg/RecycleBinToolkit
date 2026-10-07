using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Utils;

/// <summary>
/// Resolves the 8.3 short name of a path.
/// </summary>
/// <remarks>
/// The Shell's Recycle Bin is built on <c>MAX_PATH</c>, so the only way to recycle a longer path is to
/// address it by a shorter name. Short-name generation is a per-volume setting, and on a volume that
/// has it disabled no short name exists at all — this machine reports none for a 261-character path.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static unsafe class ShortPathResolver
{
    private const int BufferLength = 1024;

    /// <summary>
    /// Returns the path's 8.3 short name, or <see langword="null"/> when the volume has none.
    /// </summary>
    /// <remarks>
    /// The function legitimately returns the input unchanged when the name already fits, so a
    /// non-null result that is not shorter means there is nothing to gain.
    /// </remarks>
    internal static string? TryResolveShort(string path)
    {
        var buffer = new char[BufferLength];

        fixed (char* bufferPointer = buffer)
        fixed (char* pathPointer = path)
        {
            var length = PInvoke.GetShortPathName(new PCWSTR(pathPointer), (PWSTR)bufferPointer, BufferLength);
            if (length == 0 || length >= BufferLength)
            {
                return null;
            }

            var shortPath = new string(buffer, 0, (int)length);
            return shortPath.Length < path.Length ? shortPath : null;
        }
    }
}
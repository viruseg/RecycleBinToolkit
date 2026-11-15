using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace RecycleBinToolkit;

/// <summary>
/// The volume that physically holds what a path reaches, which is not always the volume the path is spelled
/// on.
/// </summary>
/// <remarks>
/// A junction is followed by the file system rather than by this class, so a path written under one volume
/// can name bytes on another. The answer is a volume root in the spelling the rest of the library asks
/// volumes by, because the volume gate, the bin directory and the staging directory are all derived from it.
/// <para>
/// Asking costs one open and two calls per path, and the same path is asked about once. The mount-point
/// answer is remembered for the life of one instance — one recycle call — because mounts can change between
/// calls and an answer held longer than the operation it was taken for is a stale one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal sealed class BinVolumeResolver
{
    /// <summary>
    /// Enough to ask the handle its name and no more. A file this process may not read is still a file whose
    /// volume can be named, so the open must not need read access to it.
    /// </summary>
    private const uint FileReadAttributes = 0x80;

    private const FILE_SHARE_MODE ShareMode =
        FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    private const int InitialMountPointBuffer = 512;

    private const int ErrorMoreData = 234;

    private readonly Dictionary<string, string?> _mountPoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which volume holds the object <paramref name="fullPath"/> reaches.
    /// </summary>
    /// <param name="fullPath">The item's full path, in the caller's spelling.</param>
    /// <param name="namedRoot">The volume root the path is spelled on, used when nothing better is answered.</param>
    /// <returns>The volume root that holds the bytes, or <paramref name="namedRoot"/> when Windows could not say.</returns>
    /// <remarks>
    /// A share names no volume and is answered without asking anything. Every other refusal answers the
    /// spelled root, which is what this library did before the question could be asked at all — a path
    /// whose volume cannot be resolved still recycles, into the volume it is spelled on.
    /// </remarks>
    internal string Resolve(string fullPath, string namedRoot)
    {
        if (RecycleBinVolumeState.IsUncRoot(namedRoot))
        {
            return namedRoot;
        }

        using var handle = PInvoke.CreateFile(
            LongPathStager.PathForIo(fullPath),
            FileReadAttributes,
            ShareMode,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
            null);

        if (handle.IsInvalid)
        {
            return namedRoot;
        }

        var required = PInvoke.GetFinalPathNameByHandle(handle, Span<char>.Empty, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID);

        if (required == 0)
        {
            return namedRoot;
        }

        var buffer = new char[required + 1];
        var written = PInvoke.GetFinalPathNameByHandle(handle, buffer, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID);

        if (written == 0 || written > buffer.Length)
        {
            return namedRoot;
        }

        return TryParseVolumeName(new string(buffer, 0, (int)written), out var volumeName)
            ? MountPointFor(volumeName) ?? namedRoot
            : namedRoot;
    }

    /// <summary>
    /// Reduces a final name to the volume name it begins with.
    /// </summary>
    /// <param name="finalPath">A path as <c>GetFinalPathNameByHandle</c> answered it.</param>
    /// <param name="volumeName">The volume name, separator included, when this returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="true"/> for a <c>\\?\Volume{…}\</c> path; <see langword="false"/> for every other
    /// shape, including a share and a drive-letter path.
    /// </returns>
    internal static bool TryParseVolumeName(string finalPath, out string volumeName)
    {
        volumeName = string.Empty;

        const string prefix = @"\\?\";
        const string marker = "Volume{";

        if (!finalPath.StartsWith(prefix, StringComparison.Ordinal) ||
            !finalPath.AsSpan(prefix.Length).StartsWith(marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var closeBrace = finalPath.IndexOf('}', prefix.Length + marker.Length);

        if (closeBrace < 0 ||
            closeBrace + 1 >= finalPath.Length ||
            finalPath[closeBrace + 1] != Path.DirectorySeparatorChar)
        {
            return false;
        }

        volumeName = finalPath[..(closeBrace + 2)];
        return true;
    }

    /// <summary>
    /// Picks the drive-letter root out of a volume's mount-point list.
    /// </summary>
    /// <param name="mountPoints">A double-NUL-terminated list, as the volume API answers it.</param>
    /// <returns>The first entry shaped <c>X:\</c>, or <see langword="null"/> when the list holds none.</returns>
    /// <remarks>
    /// A volume mounted only into a folder has no spelling the rest of the library reads volumes by, and the
    /// caller falls back to the root the path was spelled on.
    /// </remarks>
    internal static string? ChooseDriveLetterRoot(string mountPoints)
    {
        foreach (var entry in mountPoints.Split('\0'))
        {
            if (entry.Length == 3 && entry[1] == ':' && entry[2] == Path.DirectorySeparatorChar)
            {
                return entry;
            }
        }

        return null;
    }

    private string? MountPointFor(string volumeName)
    {
        if (_mountPoints.TryGetValue(volumeName, out var remembered))
        {
            return remembered;
        }

        var answer = QueryMountPoints(volumeName);
        _mountPoints[volumeName] = answer;
        return answer;
    }

    private static unsafe string? QueryMountPoints(string volumeName)
    {
        var buffer = new char[InitialMountPointBuffer];
        uint written;

        fixed (char* first = buffer)
        {
            if (!PInvoke.GetVolumePathNamesForVolumeName(volumeName, first, (uint)buffer.Length, out written))
            {
                if (Marshal.GetLastPInvokeError() != ErrorMoreData || written <= buffer.Length)
                {
                    return null;
                }

                buffer = new char[written];

                fixed (char* grown = buffer)
                {
                    if (!PInvoke.GetVolumePathNamesForVolumeName(volumeName, grown, (uint)buffer.Length, out written))
                    {
                        return null;
                    }
                }
            }
        }

        return ChooseDriveLetterRoot(new string(buffer, 0, (int)written));
    }
}

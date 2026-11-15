using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;

namespace RecycleBinToolkit;

/// <summary>
/// Moves a directory whose two names are spelled on different volume roots but may resolve to one volume.
/// </summary>
/// <remarks>
/// <see cref="Directory.Move(string, string)"/> refuses that pair before the file system is asked, because it
/// compares the two roots as strings — and a path through a junction names the root it is spelled on while the
/// bytes are on another. The native call resolves both names and renames when they reach the same volume;
/// when they do not it answers <c>ERROR_NOT_SAME_DEVICE</c> and the directory stays where it was, because a
/// directory that is genuinely on another volume is not worth a copy.
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class NativeDirectoryMove
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    internal static void Move(string source, string destination)
    {
        var from = LongPathStager.PathForIo(source);
        var to = LongPathStager.PathForIo(destination);

        if (string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(from, to);
            return;
        }

        if (PInvoke.MoveFile(from, to))
        {
            return;
        }

        throw Failure(Marshal.GetLastPInvokeError(), source);
    }

    /// <summary>
    /// Turns the file system's refusal into the exception its callers already filter on, keeping the code that
    /// says which refusal it was.
    /// </summary>
    private static IOException Failure(int error, string path) =>
        new(
            $"Moving {path} failed with Win32 {error}.",
            error switch
            {
                ErrorAccessDenied => unchecked((int)0x80131620),
                ErrorSharingViolation or ErrorLockViolation => unchecked((int)0x80070020),
                ErrorFileExists or ErrorAlreadyExists => unchecked((int)0x800700B7),
                ErrorNotSameDevice => unchecked((int)0x80070011),
                _ => unchecked((int)0x80070000) | error
            });
}

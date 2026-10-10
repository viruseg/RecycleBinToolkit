using System.Security;

namespace RecycleBinToolkit;

/// <summary>
/// Removes a path for good, reporting it as permanently deleted only once the file system confirms it is
/// gone.
/// </summary>
/// <remarks>
/// Two routes share this one: a path the caller has said may go because its volume cannot recycle it.
/// </remarks>
internal static class PermanentDeleter
{
    /// <summary>
    /// Removes <paramref name="target"/> outright and reports whether the file system confirms it is gone.
    /// </summary>
    /// <param name="target">The path to remove.</param>
    /// <param name="permanentlyDeleted">
    /// Receives the caller's own spelling of <paramref name="target"/>, and only once the path is
    /// confirmed gone. A path the file system would not remove is not added.
    /// </param>
    /// <param name="refused">
    /// Set on <see langword="false"/>: the file system's own error code, or 0 when nothing was raised and
    /// the path is nonetheless still there, in which case the caller has nothing better to say than that
    /// the path could not be handled.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the path is gone and has been recorded. <see langword="false"/> when
    /// nothing was recorded.
    /// </returns>
    internal static bool TryDelete(RecycleTarget target, List<string> permanentlyDeleted, out int refused)
    {
        try
        {
            // Directories need their own call, and asking for the wrong one is not a harmless mistake:
            // File.Delete on a directory raises UnauthorizedAccessException, which reads as "you do not
            // have permission" and sends the caller looking for an access problem that has nothing to do
            // with what actually went wrong.
            if (Directory.Exists(target.FullPath))
            {
                Directory.Delete(target.FullPath, recursive: true);
            }
            else
            {
                File.Delete(target.FullPath);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or SecurityException)
        {
            // Everything the file system raises for one path. Both callers document that they do not throw
            // for a per-item failure, so an exception type missing from here is a whole batch lost to one
            // file rather than one failure recorded.
            refused = ex.HResult;
            return false;
        }

        if (File.Exists(target.FullPath) || Directory.Exists(target.FullPath))
        {
            refused = 0;
            return false;
        }

        permanentlyDeleted.Add(target.InputPath);
        refused = 0;
        return true;
    }
}
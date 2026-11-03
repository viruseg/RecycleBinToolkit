namespace RecycleBinToolkit;

/// <summary>
/// Removes a path, taking the read-only attribute off it when that attribute is why the file system refused.
/// </summary>
/// <remarks>
/// A recycled item keeps the attributes it had outside the bin, and the file system refuses to delete one
/// carrying <see cref="FileAttributes.ReadOnly"/>: a read-only file answers
/// <see cref="UnauthorizedAccessException"/> with <c>ERROR_ACCESS_DENIED</c>, and a read-only directory
/// answers an <see cref="IOException"/> with no file system code behind it at all. Both shapes reach the
/// library through the bin of a real volume, so a delete that names only one of them empties some entries
/// and refuses the rest for ever.
/// <para>
/// The attribute is written only after a refusal, and only where the flag is actually set. A file held open by
/// another process is refused with <c>ERROR_SHARING_VIOLATION</c>, which no attribute answers: clearing on that
/// refusal would take the flag off a file that stays exactly where its owner left it, and a later restore
/// would put back a file that is no longer read-only.
/// </para>
/// </remarks>
internal static class ReadOnlyAwareDeleter
{
    /// <summary>
    /// The file system's answer for a path another process has open, which arrives where a read-only path
    /// answers <c>ERROR_ACCESS_DENIED</c>.
    /// </summary>
    private const int ErrorSharingViolation = 32;

    /// <summary>
    /// Descendants of a directory, whichever attributes they carry.
    /// </summary>
    /// <remarks>
    /// <see cref="EnumerationOptions.AttributesToSkip"/> is zero because a recycled payload keeps the
    /// <see cref="FileAttributes.Hidden"/> and <see cref="FileAttributes.System"/> of the item it came from,
    /// and the default would walk past every one of them.
    /// </remarks>
    private static readonly EnumerationOptions Descendants = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Removes a path, whether it is a file or a directory.
    /// </summary>
    /// <remarks>
    /// The kind is asked of the file system rather than taken from a caller: <c>File.Delete</c> on a directory
    /// raises <see cref="UnauthorizedAccessException"/>, which reads as a permission problem and sends whoever
    /// is looking for the cause of a refused delete somewhere it is not.
    /// </remarks>
    internal static void Delete(string path)
    {
        try
        {
            Attempt(path);
        }
        catch (Exception ex)
        {
            if (!TryClearForRefusal(ex, path))
            {
                throw;
            }

            Attempt(path);
        }
    }

    /// <summary>
    /// Removes a path known to be a file, without asking the file system what is there.
    /// </summary>
    /// <remarks>
    /// A listing answers <see cref="Directory.Exists"/> once per entry, and on a bin of ninety thousand that
    /// is ninety thousand metadata calls added to the sweep for a question the caller has already answered.
    /// </remarks>
    internal static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            if (!TryClearForRefusal(ex, path))
            {
                throw;
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> carries the attribute <paramref name="refusal"/> was given for, and the
    /// flag is now off it.
    /// </summary>
    /// <param name="refusal">The exception the file system raised for the delete or the overwrite.</param>
    /// <param name="path">The path it refused, or the directory that was being emptied into it.</param>
    /// <returns>
    /// <see langword="true"/> when at least one flag was cleared, which is the answer that says the same call
    /// is worth making again. <see langword="false"/> when nothing was cleared, and the caller reports the
    /// refusal it holds.
    /// </returns>
    internal static bool TryClearForRefusal(Exception refusal, string path)
    {
        if (refusal is not (UnauthorizedAccessException or IOException))
        {
            return false;
        }

        if ((refusal.HResult & 0xFFFF) == ErrorSharingViolation)
        {
            return false;
        }

        var cleared = false;

        try
        {
            cleared |= TryClearOne(path);

            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", Descendants))
                {
                    cleared |= TryClearOne(child);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A tree that cannot be walked is answered by the retry, which reports what it meets.
        }

        return cleared;
    }

    private static void Attempt(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        File.Delete(path);
    }

    private static bool TryClearOne(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                return false;
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Whatever this could not read, the delete reports.
            return false;
        }
    }
}

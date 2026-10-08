namespace RecycleBinToolkit;

/// <summary>
/// Decides whether a delete actually reached the Recycle Bin.
/// </summary>
/// <remarks>
/// The Shell reports the item it created as a side effect of the delete. When that path is present
/// it is proof the file was recycled rather than removed, which is the only way to catch a silent
/// permanent deletion. The Shell omits it for some successful deletes — a 294-character path was
/// measured doing exactly that — so its absence means "unproven", never "failed".
/// </remarks>
internal static class RecycleBinReceipt
{
    private const string RecycleBinSegment = "$RECYCLE.BIN";

    /// <summary>
    /// Checks a Shell-reported post-delete path against the original path.
    /// </summary>
    /// <param name="newItemPath">Path the Shell reported as created, or <see langword="null"/>.</param>
    /// <param name="originalPath">Path that was asked to be deleted.</param>
    /// <returns>
    /// <see langword="true"/> only when <paramref name="newItemPath"/> is a confirmed Recycle Bin
    /// location on the same volume. <see langword="null"/> returns <see langword="false"/>, meaning
    /// "not proven" rather than "proven wrong".
    /// </returns>
    internal static bool IsInsideRecycleBin(string? newItemPath, string originalPath)
    {
        if (string.IsNullOrEmpty(newItemPath))
        {
            return false;
        }

        // Both sides are reduced to Shell-form paths before anything is compared. A caller may hand over
        // an extended-length path, which names the same volume as its plain form but has a different root
        // string, and comparing the roots directly would call every such item deleted outside the
        // Recycle Bin.
        var created = RecycleTarget.StripExtendedPrefix(newItemPath);

        if (!TryVolumeRoot(RecycleTarget.StripExtendedPrefix(originalPath), out var originalRoot) ||
            !TryVolumeRoot(created, out var newRoot) ||
            !created.StartsWith(newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return originalRoot.Equals(newRoot, StringComparison.OrdinalIgnoreCase)
            && ContainsRecycleBinSegment(created.AsSpan(newRoot.Length));
    }

    /// <summary>
    /// Reads the volume root a Shell-form path belongs to.
    /// </summary>
    private static bool TryVolumeRoot(string shellPath, out string root)
    {
        try
        {
            var found = Path.GetPathRoot(shellPath);
            if (!string.IsNullOrEmpty(found))
            {
                root = found;
                return true;
            }
        }
        catch (ArgumentException)
        {
        }

        root = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether a whole directory segment after the volume root is the Recycle Bin folder.
    /// </summary>
    /// <remarks>
    /// Compared segment by segment rather than by searching the string, so that a file called
    /// <c>$RECYCLE.BIN.zip</c> in a directory the caller happened to name that way is not mistaken for
    /// the folder itself.
    /// <para>
    /// The last segment of the path is never the folder, because a receipt has to be an entry
    /// <em>inside</em> it: a path ending at a segment called <c>$RECYCLE.BIN</c> is a file with that name,
    /// and accepting it would report a permanent deletion as recycled.
    /// </para>
    /// </remarks>
    private static bool ContainsRecycleBinSegment(ReadOnlySpan<char> pathAfterRoot)
    {
        var separator = pathAfterRoot.IndexOf(Path.DirectorySeparatorChar);

        while (separator >= 0)
        {
            if (pathAfterRoot[..separator].Equals(RecycleBinSegment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            pathAfterRoot = pathAfterRoot[(separator + 1)..];
            separator = pathAfterRoot.IndexOf(Path.DirectorySeparatorChar);
        }

        return false;
    }
}
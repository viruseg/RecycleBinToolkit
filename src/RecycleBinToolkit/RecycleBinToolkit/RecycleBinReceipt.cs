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

    private static bool ContainsRecycleBinSegment(ReadOnlySpan<char> pathAfterRoot)
    {
        while (!pathAfterRoot.IsEmpty)
        {
            var separator = pathAfterRoot.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0)
            {
                separator = pathAfterRoot.IndexOf(Path.AltDirectorySeparatorChar);
            }

            var segment = separator < 0 ? pathAfterRoot : pathAfterRoot[..separator];
            if (segment.Equals(RecycleBinSegment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (separator < 0)
            {
                return false;
            }

            pathAfterRoot = pathAfterRoot[(separator + 1)..];
        }

        return false;
    }
}
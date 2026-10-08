using System.Globalization;

namespace RecycleBinToolkit;

/// <summary>
/// Holds long-path items at short paths on their own volume, so the Shell can be asked to recycle
/// them.
/// </summary>
/// <remarks>
/// The Recycle Bin is built on <c>MAX_PATH</c>, and the Shell offers no way to exceed it. The only
/// remaining route is to present a path that already fits. Because the staging directory sits on the
/// item's own volume, moving there is a rename: no bytes are copied, whatever the file size, and the
/// item lands in the Recycle Bin of the volume it came from.
/// <para>
/// A staging directory is created on demand with a generated name, and it lives only for the
/// operation that made it. Cleanup deletes the directory only when it is empty: an item that could
/// not be recycled and could not be moved back is left in place and its location is reported, because
/// deleting it would destroy the caller's data — the one outcome this library exists to prevent.
/// </para>
/// </remarks>
internal sealed class LongPathStager : IDisposable
{
    /// <summary>
    /// Prefix of every generated staging directory. A leftover is then recognisable as ours.
    /// </summary>
    internal const string NamePrefix = "$RCW-";

    private const int NameAttempts = 8;
    private const int IdempotenceAttempts = 2;

    private readonly Dictionary<string, string> _byVolumeRoot = new(StringComparer.OrdinalIgnoreCase);


    private bool _released;

    /// <summary>
    /// Generates a directory name that is not expected to be taken. The random part makes a clash with
    /// anything the user already keeps on that volume implausible.
    /// </summary>
    internal static string NewDirectoryName() => $"{NamePrefix}{Guid.NewGuid():N}";

    /// <summary>
    /// Moves an item to a short path on its own volume, creating that volume's staging directory the
    /// first time it is needed.
    /// </summary>
    /// <remarks>
    /// One attempt per call and no waiting between them. A rename that fails on a sharing violation is
    /// usually a transient hold by the indexer or antivirus, so the caller retries; retrying a whole batch
    /// at once rather than one path at a time is what keeps that backoff from being paid once per item.
    /// </remarks>
    /// <returns>
    /// The extended-length staging path, or <see langword="null"/> when the move could not be made — a
    /// volume whose root is not writable, or an item locked by another process.
    /// </returns>
    internal string? TryStageOnce(string fullPath)
    {
        ObjectDisposedException.ThrowIf(_released, this);

        var volumeRoot = Path.GetPathRoot(fullPath);
        if (volumeRoot is null)
        {
            return null;
        }

        var stagingRoot = ResolveStagingRoot(volumeRoot);
        if (stagingRoot is null)
        {
            return null;
        }

        var staged = Path.Combine(
            stagingRoot,
            string.Create(CultureInfo.InvariantCulture, $"{Guid.NewGuid():N}{Path.GetExtension(fullPath)}"));

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Move(PathForIo(fullPath), PathForIo(staged));
            }
            else
            {
                File.Move(PathForIo(fullPath), PathForIo(staged));
            }

            return PathForIo(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts a staged item back where it came from after its recycle failed.
    /// </summary>
    internal static void TryRestore(string stagedFullPath, string originalFullPath)
    {
        try
        {
            if (Directory.Exists(stagedFullPath))
            {
                Directory.Move(PathForIo(stagedFullPath), PathForIo(originalFullPath));
            }
            else
            {
                File.Move(PathForIo(stagedFullPath), PathForIo(originalFullPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Restoring is best effort: the item is already outside its original location, and the
            // staging directory is left in place with the caller told where it is.
        }
    }

    /// <summary>
    /// Removes the staging directories this instance created, and reports every one that could not be
    /// removed because something is still inside it.
    /// </summary>
    /// <remarks>
    /// All of them, not the first. A batch can span several volumes, each with its own staging
    /// directory, and a leftover on one of them is as much the caller's problem as a leftover on any
    /// other: it still holds their data, and it is still on their disk. Reporting one and dropping the
    /// rest would leave directories holding files that nothing now knows about.
    /// </remarks>
    /// <returns>
    /// The directories left in place, in the order they were created, empty when all were removed.
    /// </returns>
    internal IReadOnlyList<string> Release()
    {
        if (_released)
        {
            return [];
        }

        _released = true;

        List<string>? leftover = null;

        foreach (var directory in _byVolumeRoot.Values)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (TryRemoveEmptyDirectory(directory))
            {
                continue;
            }

            (leftover ??= []).Add(directory);
        }

        _byVolumeRoot.Clear();
        return leftover ?? (IReadOnlyList<string>)[];
    }

    /// <summary>
    /// Releases anything left. A second call is a no-op, so this is safe in a <c>finally</c>.
    /// </summary>
    public void Dispose() => Release();

    private string? ResolveStagingRoot(string volumeRoot)
    {
        if (_byVolumeRoot.TryGetValue(volumeRoot, out var existing))
        {
            return existing;
        }

        for (var attempt = 0; attempt < NameAttempts; attempt++)
        {
            var candidate = Path.Combine(volumeRoot, NewDirectoryName());

            try
            {
                if (!Directory.CreateDirectory(candidate).Exists || !IsEmpty(candidate))
                {
                    // Either something is already there under that name, or it appeared between the
                    // check and the creation. Either way it is not ours to use.
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                continue;
            }

            _byVolumeRoot[volumeRoot] = candidate;
            return candidate;
        }

        return null;
    }

    private static bool IsEmpty(string directory)
    {
        using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        return !entries.MoveNext();
    }

    private static bool TryRemoveEmptyDirectory(string directory)
    {
        for (var attempt = 0; attempt < IdempotenceAttempts; attempt++)
        {
            try
            {
                if (!IsEmpty(directory))
                {
                    return false;
                }

                Directory.Delete(directory);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process may still be holding the handle; one retry settles it.
            }
        }

        return !Directory.Exists(directory);
    }

    /// <summary>
    /// Adds the extended-length prefix where the path needs it. The .NET file APIs accept a path
    /// above <c>MAX_PATH</c> only in this form, and the Shell only in the form without it.
    /// </summary>
    /// <remarks>
    /// The prefix is not a marker that can be put in front of any path: it says "everything after this is
    /// a literal filesystem path". A UNC path has to be rewritten to <c>\\?\UNC\server\share</c>,
    /// because <c>\\?\\server\share</c> parses as a path beginning with a separator, and a device path
    /// takes no prefix at all.
    /// </remarks>
    internal static string PathForIo(string path)
    {
        if (path.Length <= MaxPathWithoutPrefix)
        {
            return path;
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + path[2..]
            : ExtendedPrefix + path;
    }

    private const string ExtendedPrefix = @"\\?\";

    private const string ExtendedUncPrefix = @"\\?\UNC\";

    private const string DevicePrefix = @"\\.\";

    private const int MaxPathWithoutPrefix = 260;
}

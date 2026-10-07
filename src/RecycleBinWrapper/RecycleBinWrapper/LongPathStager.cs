using System.Globalization;

namespace Utils;

/// <summary>
/// Holds long-path items at short paths on their own volume, so the Shell can be asked to recycle
/// them.
/// </summary>
/// <remarks>
/// The Recycle Bin is built on <c>MAX_PATH</c>, and the Shell offers no way to exceed it. The only
/// remaining route is to present a path that already fits. Because the staging directory sits on the
/// item's own volume, moving there is a rename: no bytes are copied, whatever the file size, and the
/// item lands in the Recycle Bin of the volume it came from.
/// </remarks>
internal static class LongPathStager
{
    private const string StagingDirectoryName = "$RCW";

    private static int _sequence;

    /// <summary>
    /// Staging directory for items on the volume that holds <paramref name="path"/>.
    /// </summary>
    internal static string StagingRootFor(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new ArgumentException($"'{path}' has no volume root.", nameof(path));

        return Path.Combine(root, StagingDirectoryName);
    }

    /// <summary>
    /// Moves an item to a short path on its own volume.
    /// </summary>
    /// <returns>
    /// The extended-length staging path, or <see langword="null"/> when the move could not be made —
    /// a volume whose root is not writable, or an item locked by another process.
    /// </returns>
    internal static string? TryStage(string fullPath)
    {
        var stagingRoot = StagingRootFor(fullPath);
        var staged = Path.Combine(
            stagingRoot,
            string.Create(CultureInfo.InvariantCulture, $"{Interlocked.Increment(ref _sequence):D8}{Path.GetExtension(fullPath)}"));

        try
        {
            Directory.CreateDirectory(stagingRoot);

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
    /// Puts a staged item back where it came from after its recycle failed, so nothing is left behind
    /// in the staging directory.
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
            // Restoring is best effort: the item is already outside its original location, and a
            // failure here is reported by the caller's own retry of the batch.
        }
    }

    /// <summary>
    /// Adds the extended-length prefix where the path needs it. The .NET file APIs accept a path
    /// above <c>MAX_PATH</c> only in this form, and the Shell only in the form without it.
    /// </summary>
    private static string PathForIo(string path) =>
        path.Length <= MaxPathWithoutPrefix || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path
            : @"\\?\" + path;

    private const int MaxPathWithoutPrefix = 260;
}
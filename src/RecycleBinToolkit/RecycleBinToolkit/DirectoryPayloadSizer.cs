using System.IO.Enumeration;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>Sums the lengths of the files beneath a recycled directory's payload.</summary>
/// <remarks>
/// One recursive walk and no opening: every length comes from the directory entry, so the whole measurement
/// is metadata work. Reparse points are skipped, which both keeps them out of the figure and keeps the
/// enumeration from descending into them — <see cref="EnumerationOptions.AttributesToSkip"/> is tested
/// before the recursion decision.
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class DirectoryPayloadSizer
{
    internal static long Measure(string payloadPath, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = true
        };

        var enumerable = new FileSystemEnumerable<long>(
            payloadPath,
            (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return entry.IsDirectory ? 0 : entry.Length;
            },
            options);

        long total = 0;

        foreach (var length in enumerable)
        {
            total += length;
        }

        return total;
    }
}

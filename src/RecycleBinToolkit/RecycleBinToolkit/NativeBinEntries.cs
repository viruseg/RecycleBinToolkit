using System.IO.Enumeration;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// One entry of a Recycle Bin, as one walk of the bin directory sees it.
/// </summary>
/// <param name="IndexPath">
/// Path of the <c>$I</c> record, or <see langword="null"/> when the bin holds a payload nothing describes.
/// </param>
/// <param name="PayloadPath">
/// Path of the <c>$R</c> payload, or <see langword="null"/> when the record's payload is gone.
/// </param>
/// <param name="PayloadLength">Bytes the payload holds, or <c>0</c> for a directory.</param>
/// <param name="PayloadIsDirectory">Whether the payload is a directory.</param>
internal sealed record NativeBinEntry(
    string? IndexPath,
    string? PayloadPath,
    long PayloadLength,
    bool PayloadIsDirectory);

/// <summary>
/// Walks a Recycle Bin once and pairs each entry's record with its payload.
/// </summary>
/// <remarks>
/// One walk rather than two because everything a listing shows is in it: whether an entry is a directory and
/// how large it is come from the directory entry itself, not from its record. Reading the records is left to
/// whoever asks for one, and a listing that asks for none is the ordinary case.
/// <para>
/// <see cref="EnumerationOptions.AttributesToSkip"/> is zero because both halves of an entry carry
/// <see cref="FileAttributes.Hidden"/> and <see cref="FileAttributes.System"/>, and the default would
/// enumerate nothing at all.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class NativeBinEntries
{
    /// <summary>The character every entry name starts with, before the <c>I</c> or <c>R</c> that says which half it is.</summary>
    private const char EntryMarker = '$';

    private const char IndexMarker = 'I';
    private const char PayloadMarker = 'R';

    /// <summary>
    /// How many characters a name has before the part both halves of an entry share: the marker and the half's
    /// own letter.
    /// </summary>
    private const int MarkerLength = 2;

    /// <summary>
    /// Every entry in a bin directory, paired.
    /// </summary>
    /// <param name="binDirectory">The volume's bin directory for this user.</param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <returns>
    /// The entries in the order the directory gave them. An entry appears once whether it is a whole pair, a
    /// payload without a record, or a record without a payload; which halves it has is what the two nullable
    /// paths say.
    /// </returns>
    /// <remarks>
    /// A payload that is not there is not a failure, and a record that is not there is not one either. Both are
    /// states a Recycle Bin is briefly in — a crash between two writes, an empty running against a listing —
    /// and saying which halves are present is what lets a caller decide what they mean.
    /// </remarks>
    internal static IReadOnlyList<NativeBinEntry> Collect(string binDirectory, CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var payloads = new Dictionary<string, (string Path, long Length, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = true
        };

        var enumerable = new FileSystemEnumerable<bool>(
            binDirectory,
            (ref FileSystemEntry entry) =>
            {
                TryReadHalf(entry.FileName, out var marker);

                // The part after the marker is the whole of what the two halves share, and it is unique per
                // entry — which is what makes it a key, and what keeps a duplicate from appearing twice.
                var shared = entry.FileName.Slice(MarkerLength).ToString();
                var path = Path.Combine(binDirectory, entry.FileName.ToString());

                if (!records.ContainsKey(shared) && !payloads.ContainsKey(shared))
                {
                    order.Add(shared);
                }

                if (marker == IndexMarker)
                {
                    records[shared] = path;
                    return true;
                }

                // A directory's own reported length is the directory record's size, which is not what the entry
                // occupied and not what Windows recorded for it either. Zero is the figure both agree on, and
                // the flag beside it is what tells a caller that no figure is available.
                payloads[shared] = (path, entry.IsDirectory ? 0 : entry.Length, entry.IsDirectory);
                return true;
            },
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => IsEntry(ref entry)
        };

        foreach (var _ in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var entries = new List<NativeBinEntry>(order.Count);

        foreach (var shared in order)
        {
            records.TryGetValue(shared, out var indexPath);
            payloads.TryGetValue(shared, out var payload);

            entries.Add(new NativeBinEntry(indexPath, payload.Path, payload.Length, payload.IsDirectory));
        }

        return entries;
    }

    /// <summary>
    /// Whether a directory entry is one half of a Recycle Bin entry.
    /// </summary>
    /// <remarks>
    /// A record is always a file, so a <em>directory</em> whose name starts with <c>$I</c> is not one and is
    /// passed over. The old enumerations said so with <c>!entry.IsDirectory</c>, and dropping it cost an empty
    /// something it cannot recover: deleting a directory this way raises on every run for ever, which reports
    /// a failure each time and keeps the volume out of its own results. A directory named <c>$R</c> is a
    /// different matter — a recycled directory's payload is a directory, and that is the ordinary case.
    /// </remarks>
    private static bool IsEntry(ref FileSystemEntry entry)
    {
        if (!TryReadHalf(entry.FileName, out var marker))
        {
            return false;
        }

        return marker == PayloadMarker || !entry.IsDirectory;
    }

    /// <summary>
    /// Reads which half an entry name belongs to.
    /// </summary>
    /// <remarks>
    /// Both letters are matched without regard to case because Windows does not promise to write them the same
    /// way twice, and a pair that failed to match would leave an entry neither a listing nor an empty can say
    /// anything about.
    /// </remarks>
    private static bool TryReadHalf(ReadOnlySpan<char> name, out char marker)
    {
        marker = default;

        if (name.Length <= MarkerLength || name[0] != EntryMarker)
        {
            return false;
        }

        var candidate = char.ToUpperInvariant(name[1]);

        if (candidate != IndexMarker && candidate != PayloadMarker)
        {
            return false;
        }

        marker = candidate;
        return true;
    }
}

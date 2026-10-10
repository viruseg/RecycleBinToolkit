namespace RecycleBinToolkit;

/// <summary>
/// One path to recycle, in every form the pipeline needs.
/// </summary>
/// <remarks>
/// A value type because a batch is a list of these and is held for the whole operation. As a reference
/// type every path the caller ever passes would cost an object header and three references on top of the
/// slot it occupies, which is a few megabytes of short-lived garbage across a hundred thousand paths.
/// Inline in the list, a target costs its two references and nothing else.
/// </remarks>
internal readonly struct RecycleTarget
{
    private const string ExtendedPrefix = @"\\?\";

    /// <summary>
    /// Creates a target.
    /// </summary>
    /// <param name="inputPath">The caller's path, kept verbatim so diagnostics point at what they wrote.</param>
    /// <param name="fullPath">
    /// The normalised absolute path, as validated. This is what the file APIs take, and what decides
    /// whether the item is gone after the move.
    /// </param>
    /// <param name="attributes">What the filesystem said about the path when it was validated.</param>
    /// <param name="size">
    /// The item's size in bytes as it was then, or 0 for a directory and for a path that could not be
    /// measured.
    /// </param>
    internal RecycleTarget(string inputPath, string fullPath, FileAttributes attributes, long size)
    {
        InputPath = inputPath;
        FullPath = fullPath;
        Attributes = attributes;
        Size = size;
    }

    /// <summary>The caller's path, kept verbatim so diagnostics point at what they wrote.</summary>
    public string InputPath { get; }

    /// <summary>
    /// The normalised absolute path, as validated. This is what <see cref="File"/> and
    /// <see cref="Directory"/> take, and what decides whether the item is gone after the move.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// What the filesystem said about the path when it was validated.
    /// </summary>
    /// <remarks>
    /// Validation has to ask whether the path is there, and the answer it gets back already says whether
    /// it is a directory. Carrying that forward costs four bytes on a struct that is already inline in a
    /// list, and saves a second query per path to learn the same fact — which on a
    /// batch of twenty thousand is twenty thousand filesystem round trips that already happened once.
    /// </remarks>
    public FileAttributes Attributes { get; }

    /// <summary>Whether the item is a directory, which decides whether it is moved as one.</summary>
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>
    /// The item's size in bytes when it was validated, or 0 for a directory.
    /// </summary>
    /// <remarks>
    /// Carried rather than asked for again, because validation already asked and the answer it got back
    /// included the length: one <see cref="FileInfo"/> reports attributes and size together, so asking a
    /// second time is a second round trip for a number already in hand. The record the native backend
    /// writes needs this, which is what turns it from a query of its own into a field of the target.
    /// </remarks>
    public long Size { get; }

    /// <summary>
    /// Drops every target after the first that names the same file, so two spellings of one path cannot
    /// end up with separate verdicts.
    /// </summary>
    /// <remarks>
    /// Compared on the normalised path, so <c>X:\A.BIN</c> and <c>x:\a.bin</c> meet here. A caller-spelled
    /// extended prefix (<c>\\?\X:\a.bin</c>) is a different string from the plain form and is kept as its
    /// own entry: both spellings reach the same file, the first move wins and the second is reported as a
    /// failure rather than recycled twice.
    /// </remarks>
    /// <param name="targets">Paths to collapse.</param>
    internal static IReadOnlyList<RecycleTarget> CollapseEquivalent(IReadOnlyList<RecycleTarget> targets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<RecycleTarget>(targets.Count);

        foreach (var target in targets)
        {
            if (seen.Add(target.FullPath))
            {
                unique.Add(target);
            }
        }

        return unique;
    }

    /// <summary>
    /// Whether a path names its volume by GUID rather than by a drive letter or a mount point.
    /// </summary>
    /// <remarks>
    /// Only the device form counts. <c>\\?\Volume{GUID}\…</c> is the only spelling such a volume has,
    /// and nothing above this library can read it: <c>DriveInfo</c> throws on the path, so there is no
    /// drive letter to measure, empty or report against.
    /// <para>
    /// The test is anchored to the prefix rather than to the <c>Volume{</c> text, because that text also
    /// names an ordinary directory. <c>Path.GetFullPath(@"Volume{guid}\file")</c> resolves to
    /// <c>X:\Volume{guid}\file</c> under the process's current directory, and that is a path this library
    /// handles like any other.
    /// </para>
    /// <para>
    /// Only the backslash device prefix is recognised here. The forward-slash spelling
    /// <c>//?/Volume{guid}\file</c> is the same path — <c>Path.GetFullPath</c> rewrites its separators,
    /// which is what produces this form — so a caller who typed it is answered by the check against the
    /// normalised path, which is why there are two of them.
    /// </para>
    /// <para>
    /// The earlier claim, that a relative <c>Volume{guid}\file</c> always resolves to an ordinary path, is
    /// true of every working directory but one: a process whose current directory is itself a device
    /// volume-GUID path resolves it to this form, and is refused. That is defensible — such a path is
    /// genuinely unreadable — but it is a narrowing, not a non-event.
    /// </para>
    /// </remarks>
    internal static bool IsVolumeGuidPath(string path) =>
        path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) &&
        path.AsSpan(ExtendedPrefix.Length).StartsWith("Volume{", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes an extended-length prefix for display and root comparisons.
    /// </summary>
    /// <remarks>
    /// A <c>\\?\UNC\server\share</c> prefix becomes <c>\\server\share</c>, since the UNC form is
    /// already the long-path form and needs no prefix. An extended volume GUID path keeps its prefix,
    /// because no prefixless form of it exists.
    /// </remarks>
    internal static string StripExtendedPrefix(string path)
    {
        if (!path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        var withoutPrefix = path.AsSpan(ExtendedPrefix.Length);

        if (withoutPrefix.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + withoutPrefix[4..].ToString();
        }

        return withoutPrefix.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)
            ? path
            : withoutPrefix.ToString();
    }
}
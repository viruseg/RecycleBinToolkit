namespace RecycleBinToolkit;

/// <summary>
/// One path to recycle, in every form the pipeline needs.
/// </summary>
/// <remarks>
/// A value type because a batch is a list of these and is held for the whole operation. As a reference
/// type every path the caller ever passes would cost an object header and three references on top of the
/// slot it occupies, which is a few megabytes of short-lived garbage across a hundred thousand paths for
/// an operation dominated by Shell latency. Inline in the list, a target costs its three references and
/// nothing else.
/// </remarks>
internal readonly struct RecycleTarget
{
    private const string ExtendedPrefix = @"\\?\";

    /// <summary>
    /// Creates a target.
    /// </summary>
    /// <param name="inputPath">The caller's path, kept verbatim so diagnostics point at what they wrote.</param>
    /// <param name="fullPath">
    /// The normalised absolute path, extended-length prefixed when the caller's path was. This is
    /// what the file APIs take.
    /// </param>
    /// <param name="shellPath">
    /// The same location without the <c>\\?\</c> prefix. The Shell's own path parser rejects the
    /// extended-length form with <c>E_INVALIDARG</c>, so this is what it receives.
    /// </param>
    internal RecycleTarget(string inputPath, string fullPath, string shellPath)
    {
        InputPath = inputPath;
        FullPath = fullPath;
        ShellPath = shellPath;
    }

    /// <summary>The caller's path, kept verbatim so diagnostics point at what they wrote.</summary>
    public string InputPath { get; }

    /// <summary>
    /// The normalised absolute path, extended-length prefixed when the caller's path was. This is
    /// what <see cref="File"/> and <see cref="Directory"/> take, and what decides whether the item
    /// is gone after a delete.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// The same location without the <c>\\?\</c> prefix, which is the only form the Shell accepts.
    /// </summary>
    public string ShellPath { get; }

    /// <summary>
    /// Returns a copy of this target that hands the Shell a different form of the same location.
    /// </summary>
    /// <remarks>
    /// Only the Shell-facing spelling changes. <see cref="FullPath"/> still names the caller's location,
    /// because nothing moved: this is the same file under a name the Shell can parse.
    /// </remarks>
    /// <param name="shellPath">Replacement for <see cref="ShellPath"/>.</param>
    internal RecycleTarget WithShellPath(string shellPath) => new(InputPath, FullPath, shellPath);

    /// <summary>
    /// Returns a copy of this target that names where the item actually is now, after being moved.
    /// </summary>
    /// <remarks>
    /// Both locations change, and that is the difference from <see cref="WithShellPath"/>. A staged item
    /// is not the caller's file under another name: it has been renamed, so the original location is
    /// empty. Leaving <see cref="FullPath"/> on the caller's path would make every later question asked of
    /// it answer about a place the item is no longer at — the disk check would find nothing where the item
    /// never was, and a move back would be asked to move a path onto itself.
    /// </remarks>
    /// <param name="movedFullPath">Where the item now is, in the form the file APIs take.</param>
    internal RecycleTarget WithMovedTo(string movedFullPath) => new(InputPath, movedFullPath, StripExtendedPrefix(movedFullPath));

    /// <summary>
    /// Whether a path names its volume by GUID rather than by a drive letter or a mount point.
    /// </summary>
    /// <remarks>
    /// Only the device form counts. <c>\\?\Volume{GUID}\…</c> is the only spelling such a volume has,
    /// and the Shell's item parser rejects it with <c>E_INVALIDARG</c> — measured — while nothing above
    /// this library can read it either: <c>DriveInfo</c> throws on the path and
    /// <c>SHQueryRecycleBin</c> refuses it, so there is no drive letter to measure, empty or report
    /// against.
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
    /// Removes an extended-length prefix so the Shell can parse the path.
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
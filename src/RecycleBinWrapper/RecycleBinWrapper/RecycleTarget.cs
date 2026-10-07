namespace Utils;

/// <summary>
/// One path to recycle, in every form the pipeline needs.
/// </summary>
internal sealed class RecycleTarget
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
    /// <param name="shellPath">Replacement for <see cref="ShellPath"/>.</param>
    internal RecycleTarget WithShellPath(string shellPath) => new(InputPath, FullPath, shellPath);

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
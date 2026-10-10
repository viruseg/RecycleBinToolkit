namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// The extended-length spelling of a path, whichever kind of volume it names.
/// </summary>
/// <remarks>
/// For a case that needs the prefix on a path that is <em>not</em> long, because the prefix is what the
/// case is about: a volume root written in that form, a short path handed over that way.
/// <para>
/// The rule is restated rather than called: a drive path takes <c>\\?\</c>, a share takes
/// <c>\\?\UNC\</c>. Putting the drive prefix on a UNC path instead gives <c>\\?\\server\share\</c>, which is
/// not a path at all — <see cref="Path"/> reads its root as <c>\</c>, a location on the current drive, and the
/// file APIs refuse it.
/// </para>
/// </remarks>
internal static class ExtendedPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";

    /// <summary>
    /// Returns <paramref name="path"/> in the form the file APIs take, prefixed whatever its length.
    /// </summary>
    /// <param name="path">
    /// An absolute path in the spelling the file system was given — <c>W:\…</c> or <c>\\server\share\…</c>.
    /// No volume is named here; every path in the suite comes from the run <c>--scratch</c> named.
    /// </param>
    /// <returns>
    /// <c>\\?\W:\…</c> for a drive and <c>\\?\UNC\server\share\…</c> for a share. A path that already carries
    /// a prefix is returned as it is, so a caller cannot end up with two of them.
    /// </returns>
    internal static string For(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? ExtendedUncPrefix + path[2..]
            : ExtendedPrefix + path;
    }
}
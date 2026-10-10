using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Reads the groups a listing answers with, the way the suites need to reach them.
/// </summary>
/// <remarks>
/// A listing covers one volume in most of the suites, and <see cref="OnlyEntries"/> is what those cases
/// want: it refuses rather than flattening, so a listing that grew a second group — a disk that answered when
/// it should not have — fails the case rather than passing on the flat entries of the volume it did not mean.
/// <para>
/// <see cref="Flat"/> is the escape for the cases that really are about more than one volume.
/// </para>
/// </remarks>
internal static class Listing
{
    /// <summary>
    /// The entries of the one group a listing answered for one volume.
    /// </summary>
    /// <param name="contents">The listing.</param>
    /// <param name="volumeRoot">The volume the listing was asked about.</param>
    /// <returns>The entries of that volume's group.</returns>
    public static IReadOnlyList<RecycleBinItem> OnlyEntries(RecycleBinContents contents, string volumeRoot)
    {
        var groups = GroupsOf(contents, volumeRoot);

        Check.Equal(
            1,
            groups.Count,
            $"the listing answers exactly one group for {volumeRoot}, and no other volume " +
            $"({contents.Items.Count} group(s) in all, {groups.Count} of them for it)");

        return groups[0].Items;
    }

    /// <summary>
    /// The groups a listing answered for one volume.
    /// </summary>
    /// <param name="contents">The listing.</param>
    /// <param name="volumeRoot">The volume root to match, compared without regard to case.</param>
    /// <returns>
    /// The groups belonging to that volume, in the listing's own order. Empty when the listing has none for
    /// it, which is what a listing of a volume its resolution never reached looks like.
    /// </returns>
    public static IReadOnlyList<RecycleBinVolumeContents> GroupsOf(RecycleBinContents contents, string volumeRoot)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var wanted = volumeRoot.EndsWith(Path.DirectorySeparatorChar) || volumeRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? volumeRoot
            : volumeRoot + Path.DirectorySeparatorChar;

        return contents.Items
            .Where(volume => string.Equals(volume.VolumeRoot, wanted, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Every entry of every group, in the order the listing answered them.
    /// </summary>
    /// <param name="contents">The listing.</param>
    /// <returns>The entries of all of the listing's groups.</returns>
    public static IReadOnlyList<RecycleBinItem> Flat(RecycleBinContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents.Items.SelectMany(volume => volume.Items).ToArray();
    }
}
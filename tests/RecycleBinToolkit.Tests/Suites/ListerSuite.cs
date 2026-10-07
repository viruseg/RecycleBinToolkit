using System.Runtime.Versioning;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Covers <see cref="RecycleBin.GetItemsAsync"/>: what the Shell reports about a recycled item, and
/// what the listing does with it.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class ListerSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ListerSuite." + nameof(ListedItemCarriesTheDataTheShellRecorded), ListedItemCarriesTheDataTheShellRecorded),
        new TestCase("ListerSuite." + nameof(ListedItemReportsItsPhysicalPathInsideTheRecycleBin), ListedItemReportsItsPhysicalPathInsideTheRecycleBin),
        new TestCase("ListerSuite." + nameof(ListedDirectoryIsMarkedAsOne), ListedDirectoryIsMarkedAsOne),
        new TestCase("ListerSuite." + nameof(VolumeFilterKeepsOnlyItemsFromThatVolume), VolumeFilterKeepsOnlyItemsFromThatVolume),
        new TestCase("ListerSuite." + nameof(MissingVolumeIsReportedNotThrown), MissingVolumeIsReportedNotThrown),
        new TestCase("ListerSuite." + nameof(ListingReportsEveryFileOfABatchRecycledTogether), ListingReportsEveryFileOfABatchRecycledTogether),
    ];

    /// <summary>
    /// Pins what a caller actually gets back for a file it just recycled: the path it deleted from,
    /// the name it had there, the byte count that reached the Recycle Bin, and a deletion time
    /// inside the run. A listing that dropped the original path, reported a zero size, or carried a
    /// default timestamp would still look like a listing, so each field is asserted separately.
    /// </summary>
    private static async Task ListedItemCarriesTheDataTheShellRecorded(TestContext context)
    {
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, fileSize);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file was recycled ({recycled.FirstFailure?.Stage} 0x{recycled.FirstFailure?.HResult:X8})");

        var deletedAfter = DateTimeOffset.Now.AddMinutes(-1);
        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.FirstOrDefault(i => i.OriginalPath == files[0]);
        Check.NotNull(item, $"the recycled file is listed under the path it was deleted from ({contents.Items.Count} item(s) on the machine)");
        Check.Equal(Path.GetFileName(files[0]), item!.Name, "the name is the file name it had before deletion");
        Check.Equal(fileSize, item.Size, $"the size is the {fileSize} bytes that reached the Recycle Bin");
        Check.False(item.IsDirectory, "a file is not reported as a directory");
        Check.True(item.DeletedAt >= deletedAfter, $"the deletion time is from this run, not a default value (got {item.DeletedAt:O})");

        workspace.Dispose();
    }

    /// <summary>
    /// The physical path is the one thing a caller cannot reconstruct from the other fields: the
    /// Recycle Bin renames every entry to a <c>$R</c> name, and that name is what a restore or a
    /// targeted delete needs. It must point inside the Recycle Bin of the volume the item came from.
    /// </summary>
    private static async Task ListedItemReportsItsPhysicalPathInsideTheRecycleBin(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.FirstOrDefault(i => i.OriginalPath == files[0]);
        Check.NotNull(item, "the recycled file is listed");
        Check.NotNull(item!.RecycledPath, "the physical Recycle Bin path is reported");
        Check.True(
            RecycleBinReceipt.IsInsideRecycleBin(item.RecycledPath, files[0]),
            $"the physical path is a Recycle Bin location on the item's own volume (got '{item.RecycledPath}')");
        Check.True(File.Exists(item.RecycledPath), $"the physical path is where the bytes actually are (got '{item.RecycledPath}')");

        workspace.Dispose();
    }

    /// <summary>
    /// A recycled directory keeps its contents and therefore its size, but the Shell reports the
    /// directory itself. Callers filter and total on this flag, so a directory misreported as a file
    /// would corrupt both.
    /// </summary>
    private static async Task ListedDirectoryIsMarkedAsOne(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var directory = Path.Combine(ScratchWorkspace.Root, "recycled-directory");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "inside.txt"), "content that travels with the directory");

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync([directory], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the directory was recycled");

        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.FirstOrDefault(i => i.OriginalPath == directory);
        Check.NotNull(item, "the recycled directory is listed under its original path");
        Check.True(item!.IsDirectory, "a recycled directory is reported as a directory");

        workspace.Dispose();
    }

    /// <summary>
    /// A caller working on one volume must not have to filter a machine-wide listing itself. The
    /// filter is by the volume the item was deleted from, not by the volume the <c>$R</c> file
    /// happens to sit on — those are the same volume, but only the original path records intent.
    /// </summary>
    private static async Task VolumeFilterKeepsOnlyItemsFromThatVolume(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        var all = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);
        var filtered = await RecycleBin.GetItemsAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.True(
            all.Items.Any(i => i.OriginalPath == files[0]),
            "the unfiltered listing spans the machine and holds the item");
        Check.True(
            filtered.Items.Any(i => i.OriginalPath == files[0]),
            $"the filtered listing still holds the item from {volumeRoot}");
        Check.True(
            filtered.Items.All(i => Path.GetPathRoot(i.OriginalPath) == volumeRoot),
            $"every filtered item comes from {volumeRoot}");

        workspace.Dispose();
    }

    /// <summary>
    /// A volume that is not there cannot be queried. That is environmental, so it belongs in the
    /// failure list rather than in an exception — a service listing its drives must not go down
    /// because a drive letter is unmounted.
    /// </summary>
    private static async Task MissingVolumeIsReportedNotThrown(TestContext context)
    {
        var contents = await RecycleBin.GetItemsAsync("Z:\\", context.CancellationToken).ConfigureAwait(false);

        Check.True(contents.Items.Count == 0, "nothing is reported for a volume that is not mounted");
        Check.Equal(0, contents.Failures.Count, $"a filter for an absent volume is not an enumeration failure (got {contents.Failures.Count})");
    }

    /// <summary>
    /// A batch is the shape callers use, so the listing must account for every path in it. One
    /// original path can legitimately appear more than once — deleting the same file twice leaves two
    /// separate entries with two deletion times — so uniqueness is asserted per path, not per entry.
    /// </summary>
    private static async Task ListingReportsEveryFileOfABatchRecycledTogether(TestContext context)
    {
        const int fileCount = 4;
        const int fileSize = 2048;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(fileCount, fileSize);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the batch was recycled");

        var contents = await RecycleBin.GetItemsAsync(
            Path.GetPathRoot(ScratchWorkspace.Root),
            context.CancellationToken).ConfigureAwait(false);

        foreach (var file in files)
        {
            Check.True(contents.Items.Any(i => i.OriginalPath == file), $"{file} is listed");
        }

        var listed = contents.Items.Where(i => files.Contains(i.OriginalPath)).ToArray();
        Check.True(listed.Length >= fileCount, $"every path is accounted for, allowing for repeat deletions (got {listed.Length} for {fileCount} path(s))");
        Check.True(contents.TotalSize >= fileCount * (long)fileSize, $"the total size covers the batch ({contents.TotalSize} byte(s))");

        workspace.Dispose();
    }
}

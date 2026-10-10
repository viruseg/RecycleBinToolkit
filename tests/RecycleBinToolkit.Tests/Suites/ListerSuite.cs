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
        new TestCase("ListerSuite." + nameof(VolumeFilterKeepsTheItemWhateverCaseTheRootIsWrittenIn), VolumeFilterKeepsTheItemWhateverCaseTheRootIsWrittenIn),
        new TestCase("ListerSuite." + nameof(MissingVolumeIsReportedNotThrown), MissingVolumeIsReportedNotThrown),
        new TestCase("ListerSuite." + nameof(APathInsideAVolumeIsNotAVolumeToList), APathInsideAVolumeIsNotAVolumeToList),
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
        ScratchWorkspace.RequireRecycling("an entry in the bin for the Shell to have recorded");

        var files = workspace.CreateRandomFiles(1, fileSize);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file was recycled ({recycled.FirstFailure?.Stage} 0x{recycled.FirstFailure?.HResult:X8})");

        var deletedAfter = DateTimeOffset.Now.AddMinutes(-1);
        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.FirstOrDefault(i => IsSamePath(i, files[0]));
        Check.NotNull(item, $"the recycled file is listed under the path it was deleted from ({contents.Items.Count} item(s) on the machine)");
        Check.Equal(
            Path.GetFileName(files[0]),
            Path.GetFileName(item!.OriginalPath),
            "the name it had before deletion is the last component of the path it is listed under");
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
        ScratchWorkspace.RequireRecycling("a $R file inside the bin for the listing to point at");

        var files = workspace.CreateRandomFiles(1, 4096);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.FirstOrDefault(i => IsSamePath(i, files[0]));
        Check.NotNull(item, "the recycled file is listed");
        Check.NotNull(item!.PayloadPath, "the physical Recycle Bin path is reported");
        Check.True(
            RecycleBinReceipt.IsInsideRecycleBin(item.PayloadPath, files[0]),
            $"the physical path is a Recycle Bin location on the item's own volume (got '{item.PayloadPath}')");
        Check.True(File.Exists(item.PayloadPath), $"the physical path is where the bytes actually are (got '{item.PayloadPath}')");

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

        ScratchWorkspace.RequireRecycling("a directory entry in the bin for the listing to report");

        var directory = Path.Combine(ScratchWorkspace.Root, "recycled-directory");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "inside.txt"), "content that travels with the directory");

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync([directory], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the directory was recycled");

        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.FirstOrDefault(i => IsSamePath(i, directory));
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
        ScratchWorkspace.RequireRecycling("an entry from that volume for the filter to keep");

        var files = workspace.CreateRandomFiles(1, 4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        var all = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);
        var filtered = await RecycleBin.GetItemsAsync(volumeRoot, null, context.CancellationToken).ConfigureAwait(false);

        Check.True(
            all.Items.Any(i => IsSamePath(i, files[0])),
            "the unfiltered listing spans the machine and holds the item");
        Check.True(
            filtered.Items.Any(i => IsSamePath(i, files[0])),
            $"the filtered listing still holds the item from {volumeRoot}");
        Check.True(
            filtered.Items.All(i => string.Equals(Path.GetPathRoot(i.OriginalPath), volumeRoot, StringComparison.OrdinalIgnoreCase)),
            $"every filtered item comes from {volumeRoot}");

        workspace.Dispose();
    }

    /// <summary>
    /// The root a caller holds and the spelling the Shell recorded are written by different parties, and
    /// only one of them is the Shell's answer. Windows writes the drive letter in whichever case it likes
    /// and reads every case, the library passes the recorded path through as recorded, and a caller who
    /// reached for <c>w:\</c> still has to find <c>W:\…</c>. The filter is the library's side of that: a
    /// listing filtered by a differently written root that comes back empty is a listing the caller cannot
    /// tell apart from an empty Recycle Bin.
    /// </summary>
    /// <remarks>
    /// The letter is flipped on the root the item was actually recorded under rather than on the scratch
    /// root, so the two spellings under comparison cannot come out equal by accident — the case has to
    /// survive a filter that ignores case, not one that happened to be handed its own root back.
    /// </remarks>
    private static async Task VolumeFilterKeepsTheItemWhateverCaseTheRootIsWrittenIn(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("an entry recorded under one spelling of the root for the other to keep");

        var files = workspace.CreateRandomFiles(1, 4096);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled");

        var contents = await RecycleBin.GetItemsAsync(cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var recorded = contents.Items.FirstOrDefault(i => IsSamePath(i, files[0]));
        Check.NotNull(recorded, $"the recycled file is listed whatever the Shell wrote it as ({contents.Items.Count} item(s) on the machine)");

        var asTheShellWroteIt = Path.GetPathRoot(recorded!.OriginalPath)!;
        var differentlyWritten = WithFirstLetterFlipped(asTheShellWroteIt);

        Check.True(Directory.Exists(differentlyWritten), $"\"{differentlyWritten}\" names the same volume as \"{asTheShellWroteIt}\", so a filter against it is asking about a volume and not about nothing");

        var filtered = await RecycleBin.GetItemsAsync(differentlyWritten, null, context.CancellationToken).ConfigureAwait(false);

        Check.True(
            filtered.Items.Any(i => IsSamePath(i, files[0])),
            $"the listing keeps the item when the root is written \"{differentlyWritten}\" rather than \"{asTheShellWroteIt}\"");

        workspace.Dispose();
    }

    /// <summary>
    /// A volume that is not there cannot be queried. That is environmental, so it belongs in the
    /// failure list rather than in an exception — a service listing its drives must not go down
    /// because a drive letter is unmounted.
    /// </summary>
    private static async Task MissingVolumeIsReportedNotThrown(TestContext context)
    {
        var letter = AbsentVolume.Letter();
        AbsentVolume.RequireAbsent(letter, "listing a volume that is not there");

        var contents = await RecycleBin.GetItemsAsync(AbsentVolume.RootOf(letter), null, context.CancellationToken).ConfigureAwait(false);

        Check.True(contents.Items.Count == 0, "nothing is reported for a volume that is not mounted");
        Check.Equal(0, contents.Failures.Count, $"a filter for an absent volume is not an enumeration failure (got {contents.Failures.Count})");
    }

    /// <summary>
    /// A directory inside a volume names no volume of its own, and a listing answered with an empty result
    /// reads as a Recycle Bin that holds nothing — which is a fact about the bin, not about the caller's
    /// typo. Three operations take a volume root and all three have to agree on what one is.
    /// </summary>
    /// <remarks>
    /// <see cref="RecycleBin.GetStatusAsync"/> answers the same input with
    /// <see cref="RecycleBinAvailabilityReason.VolumeNotFound"/> and <see cref="RecycleBin.EmptyAsync"/>
    /// with a <see cref="RecycleFailureStage.PathValidation"/> failure naming the path. A listing that
    /// returned neither left a caller with no way to tell a mistyped root from an empty bin.
    /// </remarks>
    private static async Task APathInsideAVolumeIsNotAVolumeToList(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a real entry on the volume for a silent widening to report");

        var file = workspace.CreateRandomFile(4096);
        var recycled = await FileRecycler
            .MoveToRecycleBinAsync([file], cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "there is an entry on the volume for the check to be about");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.Root, null, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, contents.Items.Count, "a directory inside a volume is not a volume that got listed");
        Check.Equal(1, contents.Failures.Count, "and the caller is told the path was not one");

        var failure = contents.Failures[0];
        Check.Equal(RecycleFailureStage.PathValidation, failure.Stage, "as a path that was not a volume root");
        Check.Equal(ScratchWorkspace.Root, failure.Path, "naming the path the caller passed");

        workspace.Dispose();
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
        ScratchWorkspace.RequireRecycling("every file of the batch really reaching the bin to be listed");

        var files = workspace.CreateRandomFiles(fileCount, fileSize);

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the batch was recycled");

        var contents = await RecycleBin.GetItemsAsync(
            Path.GetPathRoot(ScratchWorkspace.Root), null,
            context.CancellationToken).ConfigureAwait(false);

        foreach (var file in files)
        {
            Check.True(contents.Items.Any(i => IsSamePath(i, file)), $"{file} is listed");
        }

        var listed = contents.Items.Where(i => files.Any(file => IsSamePath(i, file))).ToArray();
        Check.True(listed.Length >= fileCount, $"every path is accounted for, allowing for repeat deletions (got {listed.Length} for {fileCount} path(s))");

        // Summed over the batch's own entries rather than over the listing. contents is the whole of the
        // volume's Recycle Bin, so TotalSize is satisfied by anything that was already in it -- which is
        // nothing this case put there. The count above is scoped the same way for the same reason.
        var batchSize = listed.Sum(item => item.Size);
        Check.True(
            batchSize >= fileCount * (long)fileSize,
            $"the size of the batch's own entries covers the batch (got {batchSize} for {fileCount} x {fileSize} byte(s), whole bin {contents.TotalSize})");

        workspace.Dispose();
    }

    /// <summary>
    /// Whether a listed item is the one that was deleted from <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Compared without regard to case, because the two sides are not written by the same party: the path
    /// is the caller's and the listing carries whatever spelling the Shell recorded, and the Shell writes
    /// the drive letter its own way however the caller passed it. Compared ordinally, this reports a
    /// listing that is correct as missing — on any machine where the caller reached for the volume in the
    /// other case, which is a spelling of the volume Windows accepts without complaint.
    /// </remarks>
    private static bool IsSamePath(RecycleBinItem item, string path) =>
        string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same root with its first letter written in the opposite case.
    /// </summary>
    /// <remarks>
    /// The drive letter for <c>W:\</c>, the server name for <c>\\server\share\</c> — whichever letter comes
    /// first is the one Windows rewrites on its own and never echoes back, so flipping it yields a
    /// spelling that certainly differs from the one a path on that volume was recorded under.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="root"/> has no letter to flip, so no second spelling of it can be written at all. A
    /// root like that would make the case compare a spelling with itself and pass whatever the filter did.
    /// </exception>
    private static string WithFirstLetterFlipped(string root)
    {
        for (var index = 0; index < root.Length; index++)
        {
            if (!char.IsLetter(root[index]))
            {
                continue;
            }

            var letter = char.IsUpper(root[index])
                ? char.ToLowerInvariant(root[index])
                : char.ToUpperInvariant(root[index]);

            return string.Concat(root.AsSpan(0, index), letter.ToString(), root.AsSpan(index + 1));
        }

        throw new InvalidOperationException($"\"{root}\" has no letter, so it has no second spelling and nothing could be compared against it.");
    }
}

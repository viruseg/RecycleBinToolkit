using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Paths that reach their bytes through a junction, and the volume the bin is decided by.
/// </summary>
/// <remarks>
/// A case here recycles into a bin and restores its own entry before it ends, found by the path its record
/// names. Nothing empties a bin: the entries other runs left there are not this suite's to remove.
/// </remarks>
internal static class LinkSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("LinkSuite." + nameof(AFileBehindAJunctionIsRecycledIntoTheVolumeThatHoldsIt), AFileBehindAJunctionIsRecycledIntoTheVolumeThatHoldsIt),
        new TestCase("LinkSuite." + nameof(TheJunctionItselfIsRecycledIntoItsOwnVolumesBinAndTheTargetStays), TheJunctionItselfIsRecycledIntoItsOwnVolumesBinAndTheTargetStays),
        new TestCase("LinkSuite." + nameof(AJunctionInsideTheRunVolumeUsesTheRunVolumesBin), AJunctionInsideTheRunVolumeUsesTheRunVolumesBin),
    ];

    /// <summary>
    /// A file reached through a junction lands in the bin of the volume its bytes are on, and its record keeps
    /// the caller's spelling so a restore comes back through the junction.
    /// </summary>
    private static async Task AFileBehindAJunctionIsRecycledIntoTheVolumeThatHoldsIt(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the caller names");
        ScratchWorkspace.RequireLinkVolumeRecycling("a bin on the volume that holds the bytes");

        var origin = CreateLinkVolumeTarget();
        var file = Path.Combine(origin.FullPath, "payload.bin");
        File.WriteAllBytes(file, new byte[4096]);
        var link = MakeJunction(origin.FullPath);

        try
        {
            var callerPath = Path.Combine(link.LinkPath, "payload.bin");
            var runBefore = BinCount(ScratchWorkspace.VolumeRoot, "the run volume before the recycle");
            var linkBefore = BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume before the recycle");

            var result = await RecycleBin.RecycleAsync([callerPath], RecycleOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the file is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(callerPath, result.RecycledPaths.Single(), "the caller's spelling is reported");
            Check.Equal(linkBefore + 1, BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume after the recycle"), "the entry landed on the volume that holds the bytes");
            Check.Equal(runBefore, BinCount(ScratchWorkspace.VolumeRoot, "the run volume after the recycle"), "and not on the volume the path is spelled on");
            Check.False(File.Exists(callerPath), "the file is gone through the link");
            Check.False(File.Exists(file), "and gone from the target tree");

            var item = await FindOwnEntry(ScratchWorkspace.LinkVolumeRoot, callerPath, context);
            Check.Equal(callerPath, item.OriginalPath, "the record keeps the caller's path");

            await RestoreOwnEntry(ScratchWorkspace.LinkVolumeRoot, callerPath, context);
            Check.True(File.Exists(callerPath), "the file is back through the link");
        }
        finally
        {
            if (Directory.Exists(link.LinkPath))
            {
                Directory.Delete(link.LinkPath);
            }

            link.Parent.Remove();
            origin.Remove();
        }
    }

    /// <summary>
    /// The link object itself belongs to the volume it is stored on, so it is recycled there and its target is
    /// left alone.
    /// </summary>
    private static async Task TheJunctionItselfIsRecycledIntoItsOwnVolumesBinAndTheTargetStays(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the link is recorded on");
        ScratchWorkspace.RequireLinkVolume("a target on another volume for the link to point at");

        var origin = CreateLinkVolumeTarget();
        File.WriteAllBytes(Path.Combine(origin.FullPath, "kept.bin"), new byte[16]);
        var link = MakeJunction(origin.FullPath);

        try
        {
            var runBefore = BinCount(ScratchWorkspace.VolumeRoot, "the run volume before the recycle");
            var linkBefore = BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume before the recycle");

            var result = await RecycleBin.RecycleAsync([link.LinkPath], RecycleOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the link is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(runBefore + 1, BinCount(ScratchWorkspace.VolumeRoot, "the run volume after the recycle"), "the link is in its own volume's bin");
            Check.Equal(linkBefore, BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume after the recycle"), "and not in the target's");
            Check.False(Directory.Exists(link.LinkPath), "the link is gone");
            Check.True(File.Exists(Path.Combine(origin.FullPath, "kept.bin")), "the target's file is untouched");

            var item = await FindOwnEntry(ScratchWorkspace.VolumeRoot, link.LinkPath, context);
            Check.Equal(link.LinkPath, item.OriginalPath, "the record keeps the link's path");

            await RestoreOwnEntry(ScratchWorkspace.VolumeRoot, link.LinkPath, context);
            Check.True(Directory.Exists(link.LinkPath), "the link is back");
        }
        finally
        {
            if (Directory.Exists(link.LinkPath))
            {
                Directory.Delete(link.LinkPath);
            }

            link.Parent.Remove();
            origin.Remove();
        }
    }

    /// <summary>
    /// A junction whose target is on the run volume is an ordinary item on the run volume's own bin, so the
    /// suite runs the same journey without a second volume.
    /// </summary>
    private static async Task AJunctionInsideTheRunVolumeUsesTheRunVolumesBin(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the link is recorded on");

        var origin = CreateRunVolumeTarget();
        File.WriteAllBytes(Path.Combine(origin.FullPath, "kept.bin"), new byte[16]);
        var link = MakeJunction(origin.FullPath);

        try
        {
            var before = BinCount(ScratchWorkspace.VolumeRoot, "the run volume before the recycle");

            var result = await RecycleBin.RecycleAsync([link.LinkPath], RecycleOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the link is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(before + 1, BinCount(ScratchWorkspace.VolumeRoot, "the run volume after the recycle"), "the link is in the run volume's bin");
            Check.False(Directory.Exists(link.LinkPath), "the link is gone");
            Check.True(File.Exists(Path.Combine(origin.FullPath, "kept.bin")), "the target's file is untouched");

            var item = await FindOwnEntry(ScratchWorkspace.VolumeRoot, link.LinkPath, context);
            Check.Equal(link.LinkPath, item.OriginalPath, "the record keeps the link's path");

            await RestoreOwnEntry(ScratchWorkspace.VolumeRoot, link.LinkPath, context);
            Check.True(Directory.Exists(link.LinkPath), "the link is back");
        }
        finally
        {
            if (Directory.Exists(link.LinkPath))
            {
                Directory.Delete(link.LinkPath);
            }

            link.Parent.Remove();
            origin.Remove();
        }
    }

    private sealed record LinkFixture(ScratchDirectory Parent, string LinkPath, string TargetPath);

    private static ScratchDirectory CreateLinkVolumeTarget() => ScratchDirectory.Claim(ScratchWorkspace.LinkRoot);

    private static ScratchDirectory CreateRunVolumeTarget() => ScratchDirectory.Claim(ScratchWorkspace.Root);

    private static LinkFixture MakeJunction(string targetPath)
    {
        var parent = ScratchDirectory.Claim(ScratchWorkspace.Root);
        var linkPath = Path.Combine(parent.FullPath, "junc");

        try
        {
            Junction.Create(linkPath, targetPath);
        }
        catch
        {
            parent.Remove();
            throw;
        }

        return new LinkFixture(parent, linkPath, targetPath);
    }

    private static async Task<RecycleBinItem> FindOwnEntry(string binRoot, string recordPath, TestContext context)
    {
        var listing = await RecycleBin.GetItemsAsync(binRoot, cancellationToken: context.CancellationToken);

        var named = Listing
            .OnlyEntries(listing, binRoot)
            .Where(entry => string.Equals(entry.OriginalPath, recordPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Check.Equal(1, named.Length, $"exactly one entry in {binRoot} was deleted from {recordPath}");

        return named[0];
    }

    private static async Task RestoreOwnEntry(string binRoot, string recordPath, TestContext context)
    {
        var item = await FindOwnEntry(binRoot, recordPath, context);

        var result = await RecycleBin.RestoreAsync([item], RestoreOptions.Default, context.CancellationToken);

        Check.True(
            result.Succeeded,
            $"the entry at {recordPath} is restored ({Describe(result.Failures.FirstOrDefault())})");
    }

    private static long BinCount(string binRoot, string because) => RecycleBinProbe.MeasureOrFail(binRoot, because).Items;

    private static string Describe<TStage>(RecycleFailure<TStage>? failure)
        where TStage : struct, Enum =>
        failure is null ? "none" : $"{failure.Stage} 0x{failure.HResult:X8} {failure.Path}";
}

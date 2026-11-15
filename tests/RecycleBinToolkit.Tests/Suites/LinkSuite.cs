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
        new TestCase("LinkSuite." + nameof(ADirectoryBehindAJunctionIsRecycledIntoTheVolumeThatHoldsIt), ADirectoryBehindAJunctionIsRecycledIntoTheVolumeThatHoldsIt),
        new TestCase("LinkSuite." + nameof(ARecycledTreeCarriesTheJunctionAndLeavesItsTarget), ARecycledTreeCarriesTheJunctionAndLeavesItsTarget),
        new TestCase("LinkSuite." + nameof(ARestoredFileBehindAJunctionGoesBackThroughTheJunction), ARestoredFileBehindAJunctionGoesBackThroughTheJunction),
        new TestCase("LinkSuite." + nameof(ARestoredDirectoryBehindAJunctionGoesBackThroughTheJunction), ARestoredDirectoryBehindAJunctionGoesBackThroughTheJunction),
        new TestCase("LinkSuite." + nameof(AMergedTreeBehindAJunctionKeepsTheLinkInsideIt), AMergedTreeBehindAJunctionKeepsTheLinkInsideIt),
        new TestCase("LinkSuite." + nameof(ALongPathBehindAJunctionStagesOnItsOwnVolumesBin), ALongPathBehindAJunctionStagesOnItsOwnVolumesBin),
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

    /// <summary>
    /// A directory reached through a junction is recycled into the bin of the volume its bytes are on, and its
    /// contents come back through the junction.
    /// </summary>
    private static async Task ADirectoryBehindAJunctionIsRecycledIntoTheVolumeThatHoldsIt(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the caller names");
        ScratchWorkspace.RequireLinkVolumeRecycling("a bin on the volume that holds the bytes");

        var origin = CreateLinkVolumeTarget();
        Directory.CreateDirectory(Path.Combine(origin.FullPath, "inside"));
        File.WriteAllBytes(Path.Combine(origin.FullPath, "inside", "payload.bin"), new byte[4096]);
        var link = MakeJunction(origin.FullPath);

        try
        {
            var callerPath = Path.Combine(link.LinkPath, "inside");
            var runBefore = BinCount(ScratchWorkspace.VolumeRoot, "the run volume before the recycle");
            var linkBefore = BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume before the recycle");

            var result = await RecycleBin.RecycleAsync([callerPath], RecycleOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the directory is recycled through the link (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(linkBefore + 1, BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume after the recycle"), "the payload is on the volume that holds it");
            Check.Equal(runBefore, BinCount(ScratchWorkspace.VolumeRoot, "the run volume after the recycle"), "and not on the volume it is spelled on");
            Check.False(Directory.Exists(callerPath), "the directory is gone through the link");
            Check.Equal(
                callerPath,
                (await FindOwnEntry(ScratchWorkspace.LinkVolumeRoot, callerPath, context)).OriginalPath,
                "the record keeps the caller's path");

            await RestoreOwnEntry(ScratchWorkspace.LinkVolumeRoot, callerPath, context);
            Check.True(File.Exists(Path.Combine(callerPath, "payload.bin")), "and the restore brings its contents back through the link");
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
    /// A tree holding a junction is recycled as one object: the junction stays a link, and its target is not
    /// part of the tree.
    /// </summary>
    private static async Task ARecycledTreeCarriesTheJunctionAndLeavesItsTarget(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin for the tree");
        ScratchWorkspace.RequireLinkVolume("a target outside the tree for its junction to point at");

        var parent = ScratchDirectory.Claim(ScratchWorkspace.Root);
        var tree = Path.Combine(parent.FullPath, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "plain.bin"), new byte[16]);
        var target = CreateLinkVolumeTarget();
        File.WriteAllBytes(Path.Combine(target.FullPath, "kept.bin"), new byte[16]);
        Junction.Create(Path.Combine(tree, "junc"), target.FullPath);

        try
        {
            var runBefore = BinCount(ScratchWorkspace.VolumeRoot, "the run volume before the recycle");
            var linkBefore = BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume before the recycle");

            var result = await RecycleBin.RecycleAsync([tree], RecycleOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the tree is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(runBefore + 1, BinCount(ScratchWorkspace.VolumeRoot, "the run volume after the recycle"), "the tree is one entry in its own volume's bin");
            Check.Equal(linkBefore, BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume after the recycle"), "and the target contributes nothing");
            Check.False(Directory.Exists(tree), "the tree left its place");
            Check.True(File.Exists(Path.Combine(target.FullPath, "kept.bin")), "the target is untouched");

            await RestoreOwnEntry(ScratchWorkspace.VolumeRoot, tree, context);
            Check.True(File.Exists(Path.Combine(tree, "plain.bin")), "the ordinary file came back");
            Check.True(
                (File.GetAttributes(Path.Combine(tree, "junc")) & FileAttributes.ReparsePoint) != 0,
                "the junction inside comes back as a link");
        }
        finally
        {
            if (Directory.Exists(Path.Combine(tree, "junc")))
            {
                Directory.Delete(Path.Combine(tree, "junc"));
            }

            parent.Remove();
            target.Remove();
        }
    }

    /// <summary>
    /// A file recycled through a junction comes back to the path its record names, which is the caller's
    /// spelling and still leads through the junction.
    /// </summary>
    private static async Task ARestoredFileBehindAJunctionGoesBackThroughTheJunction(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the caller names");
        ScratchWorkspace.RequireLinkVolumeRecycling("a bin on the volume that holds the bytes");

        var origin = CreateLinkVolumeTarget();
        var file = Path.Combine(origin.FullPath, "payload.bin");
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(file, bytes);
        var link = MakeJunction(origin.FullPath);

        try
        {
            var callerPath = Path.Combine(link.LinkPath, "payload.bin");
            var linkBefore = BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume before the recycle");

            var recycled = await RecycleBin.RecycleAsync([callerPath], RecycleOptions.Default, context.CancellationToken);
            Check.True(recycled.Succeeded, $"the file reached the link volume's bin (first failure: {Describe(recycled.FirstFailure)})");

            var item = await FindOwnEntry(ScratchWorkspace.LinkVolumeRoot, callerPath, context);
            var result = await RecycleBin.RestoreAsync([item], RestoreOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the file is restored ({Describe(result.Failures.FirstOrDefault())})");
            Check.True(File.Exists(callerPath), "the file is back at the caller's path, through the link");
            Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(callerPath)), "with its bytes");
            Check.Equal(linkBefore, BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume after the restore"), "and its record is gone");
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
    /// A directory recycled through a junction comes back through it, contents included.
    /// </summary>
    private static async Task ARestoredDirectoryBehindAJunctionGoesBackThroughTheJunction(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the caller names");
        ScratchWorkspace.RequireLinkVolumeRecycling("a bin on the volume that holds the bytes");

        var origin = CreateLinkVolumeTarget();
        Directory.CreateDirectory(Path.Combine(origin.FullPath, "inside"));
        var file = Path.Combine(origin.FullPath, "inside", "payload.bin");
        var bytes = new byte[4096];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(file, bytes);
        var link = MakeJunction(origin.FullPath);

        try
        {
            var callerPath = Path.Combine(link.LinkPath, "inside");
            var linkBefore = BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume before the recycle");

            var recycled = await RecycleBin.RecycleAsync([callerPath], RecycleOptions.Default, context.CancellationToken);
            Check.True(recycled.Succeeded, $"the directory reached the link volume's bin (first failure: {Describe(recycled.FirstFailure)})");

            var item = await FindOwnEntry(ScratchWorkspace.LinkVolumeRoot, callerPath, context);
            var result = await RecycleBin.RestoreAsync([item], RestoreOptions.Default, context.CancellationToken);

            Check.True(result.Succeeded, $"the directory is restored ({Describe(result.Failures.FirstOrDefault())})");
            Check.True(Directory.Exists(callerPath), "the directory is back at the caller's path, through the link");
            Check.True(File.Exists(Path.Combine(callerPath, "payload.bin")), "with its file");
            Check.True(
                bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(callerPath, "payload.bin"))),
                "and its bytes");
            Check.Equal(linkBefore, BinCount(ScratchWorkspace.LinkVolumeRoot, "the link volume after the restore"), "and its record is gone");
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
    /// A tree merged back into a destination that still holds it keeps the junction inside it a link, and the
    /// merge does not cross into what that link points at.
    /// </summary>
    private static async Task AMergedTreeBehindAJunctionKeepsTheLinkInsideIt(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin for the tree");
        ScratchWorkspace.RequireLinkVolume("a target outside the tree for its junction to point at");

        var parent = ScratchDirectory.Claim(ScratchWorkspace.Root);
        var tree = Path.Combine(parent.FullPath, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "plain.bin"), new byte[16]);
        var mergeOrigin = CreateLinkVolumeTarget();
        File.WriteAllBytes(Path.Combine(mergeOrigin.FullPath, "kept.bin"), new byte[16]);
        Junction.Create(Path.Combine(tree, "junc"), mergeOrigin.FullPath);

        try
        {
            var recycled = await RecycleBin.RecycleAsync([tree], RecycleOptions.Default, context.CancellationToken);
            Check.True(recycled.Succeeded, $"the tree reached the bin (first failure: {Describe(recycled.FirstFailure)})");

            Directory.CreateDirectory(tree);

            var options = new RestoreOptions { DirectoryConflict = RestoreDirectoryConflict.MergeSkip };
            var item = await FindOwnEntry(ScratchWorkspace.VolumeRoot, tree, context);
            var result = await RecycleBin.RestoreAsync([item], options, context.CancellationToken);

            Check.True(result.Succeeded, $"the tree is merged back ({Describe(result.Failures.FirstOrDefault())})");
            Check.True(File.Exists(Path.Combine(tree, "plain.bin")), "the ordinary file is merged");
            Check.True(
                (File.GetAttributes(Path.Combine(tree, "junc")) & FileAttributes.ReparsePoint) != 0,
                "the junction is moved as a link");
            Check.True(File.Exists(Path.Combine(mergeOrigin.FullPath, "kept.bin")), "and nothing crossed into its target");
        }
        finally
        {
            if (Directory.Exists(Path.Combine(tree, "junc")))
            {
                Directory.Delete(Path.Combine(tree, "junc"));
            }

            parent.Remove();
            mergeOrigin.Remove();
        }
    }

    /// <summary>
    /// A path the record cannot name is staged on the volume that holds its bytes, not on the volume its
    /// spelling belongs to — otherwise the staging move is a copy and the record names a path the item does
    /// not live at.
    /// </summary>
    private static async Task ALongPathBehindAJunctionStagesOnItsOwnVolumesBin(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a bin on the volume the caller names");
        ScratchWorkspace.RequireLinkVolumeRecycling("a bin on the volume that holds the bytes");

        var origin = CreateLinkVolumeTarget();
        var deep = BuildChain(origin.FullPath, NativeIndexFile.LegacyPathCapacity + 50);
        File.WriteAllBytes(Path.Combine(deep, "payload.bin"), new byte[16]);
        var link = MakeJunction(origin.FullPath);
        var callerPath = Path.Combine(link.LinkPath, Path.GetRelativePath(origin.FullPath, deep), "payload.bin");

        Check.True(
            callerPath.Length > NativeIndexFile.LegacyPathCapacity,
            $"the caller's path is past the record ({callerPath.Length} characters)");

        IndexFormat.ForceVersionOne = true;
        NativeRecycleRunner.FaultAfterStaging = true;
        NativeRecycleRunner.FaultBeforePutBack = true;
        try
        {
            var options = new RecycleOptions { LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle };
            var result = await RecycleBin.RecycleAsync([callerPath], options, context.CancellationToken);

            var staged = result.LeftoverStagingDirectories.Single();
            Check.True(
                string.Equals(Path.GetPathRoot(staged), ScratchWorkspace.LinkVolumeRoot, StringComparison.OrdinalIgnoreCase),
                $"the staging directory is on the volume that holds the bytes: {staged}");
            Check.True(Directory.Exists(staged), "and it still holds the item");
            Check.True(Path.GetFileName(staged).StartsWith(LongPathStager.NamePrefix, StringComparison.Ordinal), "under the name this library uses");
            Check.Equal(
                0,
                Directory.GetDirectories(ScratchWorkspace.VolumeRoot, LongPathStager.NamePrefix + "*").Length,
                "with nothing left on the volume the path is spelled on");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
            NativeRecycleRunner.FaultAfterStaging = false;
            NativeRecycleRunner.FaultBeforePutBack = false;

            foreach (var root in new[] { ScratchWorkspace.LinkVolumeRoot, ScratchWorkspace.VolumeRoot })
            {
                foreach (var leftover in Directory.GetDirectories(root, LongPathStager.NamePrefix + "*"))
                {
                    Directory.Delete(leftover, recursive: true);
                }
            }

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

    private static string BuildChain(string root, int characters)
    {
        var deepest = root.TrimEnd(Path.DirectorySeparatorChar);
        var component = new string('d', 118);

        while (deepest.Length < characters)
        {
            deepest = Path.Combine(deepest, component);
            Directory.CreateDirectory(deepest);
        }

        return deepest;
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

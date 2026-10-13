using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Paths longer than the Recycle Bin's index record can name on a machine written in the older of the two
/// formats, and what this library does with each.
/// </summary>
/// <remarks>
/// Every case here sets <see cref="IndexFormat.ForceVersionOne"/> first, because that is the only way to
/// reach the older format on a machine that can read the newer one. They read as one story about long paths
/// rather than as seven separate facts, which is why they are a suite of their own.
/// <para>
/// Where a case provokes a staging directory, it removes it afterwards. The library creates those
/// directories; a case cleans them up.
/// </para>
/// </remarks>
internal static class LongPathSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("LongPathSuite." + nameof(PathUnderTheCapacityStillRecyclesNormally), PathUnderTheCapacityStillRecyclesNormally),
        new TestCase("LongPathSuite." + nameof(ARecycledEntryNamesItsOriginalPathAndRoundTrips), ARecycledEntryNamesItsOriginalPathAndRoundTrips),
        new TestCase("LongPathSuite." + nameof(ShortenPathAndRecyclePutsTheBytesInTheBin), ShortenPathAndRecyclePutsTheBytesInTheBin),
        new TestCase("LongPathSuite." + nameof(ShortenPathAndRecycleRemovesTheStagingDirectory), ShortenPathAndRecycleRemovesTheStagingDirectory),
        new TestCase("LongPathSuite." + nameof(StagingDirectoryNamesAreUniqueAndShortEnough), StagingDirectoryNamesAreUniqueAndShortEnough),
        new TestCase("LongPathSuite." + nameof(ThrowPolicyObeysTheGlobalSwitch), ThrowPolicyObeysTheGlobalSwitch),
        new TestCase("LongPathSuite." + nameof(DeletePermanentlyReportsEveryLongPathItHandled), DeletePermanentlyReportsEveryLongPathItHandled),
        new TestCase("LongPathSuite." + nameof(AVersionOneEntryRestoresToItsRecordedPath), AVersionOneEntryRestoresToItsRecordedPath),
        new TestCase("LongPathSuite." + nameof(ARefusedStagingRenameIsAskedForAgain), ARefusedStagingRenameIsAskedForAgain),
    ];

    /// <summary>Characters the record in use can name.</summary>
    private static int Capacity => NativeIndexFile.LegacyPathCapacity;

    /// <summary>
    /// A path inside the limit takes the ordinary route and never reaches the policy. Everything the long
    /// path machinery does is under the limit's shadow, and a case that did not hold that would let it
    /// reach paths it has no business touching.
    /// </summary>
    private static async Task PathUnderTheCapacityStillRecyclesNormally(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the entry to land in");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var stagingBefore = StagingDirectoryCount();

            var path = Path.Combine(ScratchWorkspace.Root, $"under-capacity-{RunTag}.bin");
            File.WriteAllBytes(path, Content);

            var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

            Check.True(result.Succeeded, $"the file is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(1, result.RecycledPaths.Count, "and reported as recycled");
            Check.Equal(0, result.Failures.Count, "with nothing failed");
            Check.Equal(0, result.LeftoverStagingDirectories.Count, "and nothing stranded");

            var record = FindNewestRecord(path);
            Check.Equal(path, record.OriginalPath, "the record names the path the caller passed, unchanged");
            Check.Equal(stagingBefore, StagingDirectoryCount(), "and no staging directory was created for it");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The whole point of writing an older format: an entry a caller's own Explorer can read. The record
    /// declares the version that machine reads, and reading it back gives the caller's path.
    /// </summary>
    private static async Task ARecycledEntryNamesItsOriginalPathAndRoundTrips(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the entry to land in");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var path = CreateFile("round-trip");
            var size = new FileInfo(path).Length;

            var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

            Check.True(result.Succeeded, $"the file is recycled (first failure: {Describe(result.FirstFailure)})");

            var indexPath = RecordPathOf(path);
            Check.Equal(IndexFormat.Legacy, NativeIndexFile.ReadVersion(indexPath), "the record declares the older format");
            Check.True(NativeIndexFile.TryRead(indexPath, out var record), "and this library reads back what it wrote");
            Check.Equal(path, record.OriginalPath, "naming the path the caller passed");
            Check.Equal(size, record.Size, "and the size it had");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A path the record cannot name is moved to a short path on its own volume and recycled from there. The
    /// bytes end up in the Recycle Bin of the volume the item came from, which is the whole point of
    /// staging there rather than somewhere shorter-lived.
    /// </summary>
    private static async Task ShortenPathAndRecyclePutsTheBytesInTheBin(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the item to land in");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var path = CreateDeepFile(Capacity + 50);
            var size = new FileInfo(path).Length;
            var options = new RecycleOptions { LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle };

            var result = await RecycleBin.RecycleAsync([path], options, context.CancellationToken).ConfigureAwait(false);

            Check.True(result.Succeeded, $"the item is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.False(File.Exists(path), "and is gone from the path the caller named");

            var staged = FindStagedRecord(size);
            Check.Equal(ScratchWorkspace.VolumeRoot, Path.GetPathRoot(staged), "the entry landed in the Recycle Bin of the item's own volume");
            Check.True(staged.Length <= Capacity, $"and the record names a path it can hold ({staged.Length} of {Capacity})");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
            RemoveStagingDirectories();
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A staging directory is this library's to make and this library's to remove. One left in the volume
    /// root after a run is litter in a place a user looks at.
    /// </summary>
    private static async Task ShortenPathAndRecycleRemovesTheStagingDirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the item to land in");

        var before = StagingDirectoryCount();

        IndexFormat.ForceVersionOne = true;
        try
        {
            var path = CreateDeepFile(Capacity + 50);
            var options = new RecycleOptions { LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle };

            var result = await RecycleBin.RecycleAsync([path], options, context.CancellationToken).ConfigureAwait(false);

            Check.True(result.Succeeded, $"the item is recycled (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(0, result.LeftoverStagingDirectories.Count, "and the result reports nothing left behind");
            Check.Equal(before, StagingDirectoryCount(), "and the volume root holds no staging directory afterwards");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
            RemoveStagingDirectories();
        }

        workspace.Dispose();
    }

    /// <summary>
    /// Names are generated, so two items in one batch cannot collide, and each is short enough that moving
    /// an item there is itself the thing being tested.
    /// </summary>
    private static async Task StagingDirectoryNamesAreUniqueAndShortEnough(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the items to land in");

        Check.True(LongPathStager.NewDirectoryName().StartsWith(LongPathStager.NamePrefix, StringComparison.Ordinal),
            "every staging directory is recognisable as this library's");
        Check.NotEqual(LongPathStager.NewDirectoryName(), LongPathStager.NewDirectoryName(), "and no two are the same");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var paths = Enumerable.Range(1, 4).Select(i => CreateDeepFile(Capacity + 50 + i)).ToArray();
            var options = new RecycleOptions { LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle };

            var result = await RecycleBin.RecycleAsync(paths, options, context.CancellationToken).ConfigureAwait(false);

            Check.True(result.Succeeded, $"all four are recycled (first failure: {Describe(result.FirstFailure)})");

            foreach (var path in paths)
            {
                Check.False(File.Exists(path), $"the item at {path.Length} characters is gone from where it was");
            }
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
            RemoveStagingDirectories();
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The global switch has the last word on whether a refusal raises. Turned off, the refusal is data on
    /// the result; turned on, it is an exception and the file is still where it was.
    /// </summary>
    private static async Task ThrowPolicyObeysTheGlobalSwitch(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the refusal to be measured against");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var path = CreateDeepFile(Capacity + 50);

            await Check.ThrowsAsync<PathTooLongException>(
                () => RecycleBin.RecycleAsync(path, RecycleOptions.Default, context.CancellationToken),
                "the single-path overload raises on a path the record cannot name, by default");

            Check.False(
                await RecycleBin.RecycleAsync(path, new RecycleOptions { ThrowOnError = false }, context.CancellationToken).ConfigureAwait(false),
                "and answers false instead when the caller turned throwing off");

            Check.True(File.Exists(path), "either way the file is untouched");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The caller's own spelling reaches the result, for every path handled, so an outcome they did not
    /// ask to hear about is the one thing they cannot act on.
    /// </summary>
    private static async Task DeletePermanentlyReportsEveryLongPathItHandled(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the refusals to be measured against");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var paths = new[] { CreateDeepFile(Capacity + 50), CreateDeepFile(Capacity + 60), CreateDeepFile(Capacity + 70) };
            var options = new RecycleOptions { LongPathFailureAction = LongPathFailureAction.DeletePermanently };

            var result = await RecycleBin.RecycleAsync(paths, options, context.CancellationToken).ConfigureAwait(false);

            Check.True(result.Succeeded, $"all three are gone (first failure: {Describe(result.FirstFailure)})");
            Check.Equal(paths.Length, result.PermanentlyDeletedPaths.Count, "and every one is reported");

            foreach (var path in paths)
            {
                Check.True(result.PermanentlyDeletedPaths.Contains(path, StringComparer.OrdinalIgnoreCase),
                    $"including {path.Length}-character {Path.GetFileName(path[..Math.Min(path.Length, 60)])}…");
                Check.False(File.Exists(path), "and it is gone from disk");
            }
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The round trip a caller's own Explorer performs: read the record, put the payload back where it
    /// says. An entry that cannot be restored is an entry the Recycle Bin exists to prevent.
    /// </summary>
    private static async Task AVersionOneEntryRestoresToItsRecordedPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the entry to be restored from");

        IndexFormat.ForceVersionOne = true;
        try
        {
            var path = CreateFile("restored-from-v1");
            var content = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);

            var recycled = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
            Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

            var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, null, context.CancellationToken).ConfigureAwait(false);
            var listed = Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot)
                .Single(item => string.Equals(item.OriginalPath, path, StringComparison.OrdinalIgnoreCase));

            var restored = await RecycleBin.RestoreAsync([listed], null, context.CancellationToken).ConfigureAwait(false);

            Check.True(restored.Succeeded, $"the entry is restored ({Describe(restored.Failures.FirstOrDefault())})");
            Check.True(File.Exists(path), "and the file is back where the record said it came from");
            Check.Equal(content.Length, new FileInfo(path).Length, "with the bytes it had");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A rename can lose a race with the indexer or antivirus, and that is the one failure here which is a
    /// property of the machine's moment rather than of the path. It has to be asked for again.
    /// </summary>
    /// <remarks>
    /// The hold is deterministic: a file open without a delete share cannot be renamed, so the move fails
    /// on every round. What is pinned is that more than one round happened, which the outcome alone cannot
    /// show — a path refused on the first attempt and a path refused after exhausting them look the same.
    /// </remarks>
    private static async Task ARefusedStagingRenameIsAskedForAgain(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the attempt to be measured against");

        var path = CreateDeepFile(Capacity + 50);
        var options = new RecycleOptions { LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle };

        IndexFormat.ForceVersionOne = true;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = await RecycleBin.RecycleAsync([path], options, context.CancellationToken).ConfigureAwait(false);

                Check.False(result.Succeeded, "a rename that cannot be made does not recycle the item");
                Check.True(result.FirstFailure is not null, "and is reported as a failure");
            }

            Check.True(
                NativeRecycleRunner.StageAttemptsMade > 1,
                $"the rename was asked for more than once before the path was given up on ({NativeRecycleRunner.StageAttemptsMade} attempts)");

            Check.True(File.Exists(path), "and the item is where the caller left it");
        }
        finally
        {
            IndexFormat.ForceVersionOne = false;
            RemoveStagingDirectories();
        }

        workspace.Dispose();
    }

    private static int StagingDirectoryCount()
    {
        if (!Directory.Exists(ScratchWorkspace.VolumeRoot))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateDirectories(ScratchWorkspace.VolumeRoot, LongPathStager.NamePrefix + "*", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Removes the staging directories this run provoked. The library creates them; a case cleans them up,
    /// which is deleting and not creating.
    /// </summary>
    private static void RemoveStagingDirectories()
    {
        if (!Directory.Exists(ScratchWorkspace.VolumeRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(ScratchWorkspace.VolumeRoot, LongPathStager.NamePrefix + "*", SearchOption.TopDirectoryOnly))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The newest record in the bin whose original path is <paramref name="originalPath"/>.</summary>
    private static NativeIndexRecord FindNewestRecord(string originalPath)
    {
        var matching = IndexRecords()
            .Where(path => NativeIndexFile.TryRead(path, out var record)
                && string.Equals(record.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matching.Length == 0)
        {
            throw new CheckFailedException($"no record in {BinDirectory} names {originalPath}");
        }

        NativeIndexFile.TryRead(matching.MaxBy(File.GetLastWriteTimeUtc)!, out var newest);
        return newest;
    }

    /// <summary>The file path of the record naming <paramref name="originalPath"/>.</summary>
    private static string RecordPathOf(string originalPath)
    {
        var matching = IndexRecords()
            .Where(path => NativeIndexFile.TryRead(path, out var record)
                && string.Equals(record.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matching.Length == 0
            ? throw new CheckFailedException($"no record in {BinDirectory} names {originalPath}")
            : matching.MaxBy(File.GetLastWriteTimeUtc)!;
    }

    /// <summary>
    /// The path an entry staged by this run was recorded under, found by which record carries this run's
    /// size and names a staging path.
    /// </summary>
    private static string FindStagedRecord(long size)
    {
        var prefix = Path.Combine(ScratchWorkspace.VolumeRoot, LongPathStager.NamePrefix);

        foreach (var path in IndexRecords())
        {
            if (NativeIndexFile.TryRead(path, out var record)
                && record.Size == size
                && record.OriginalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return record.OriginalPath;
            }
        }

        throw new CheckFailedException($"no record in {BinDirectory} of size {size} names a staging path under {prefix}");
    }

    /// <summary>
    /// A file whose whole path is at least <paramref name="minimumLength"/> characters, built as a chain
    /// because one component cannot exceed 255.
    /// </summary>
    private static string CreateDeepFile(int minimumLength)
    {
        var directory = ScratchWorkspace.Root;
        var component = new string('d', 118);
        var depth = 0;

        while (directory.Length + 105 < minimumLength)
        {
            var deeper = Path.Combine(directory, component);
            Directory.CreateDirectory(deeper);
            directory = deeper;
            depth++;
        }

        // The discriminator is in the leaf, not the chain: two calls asking for lengths that round to the
        // same depth would otherwise build the same path, and a batch of three identical paths collapses
        // to one before any of them is looked at.
        var path = Path.Combine(directory, new string('f', 60) + "-" + RunTag + "-" + Interlocked.Increment(ref _sequence) + "-" + depth + ".bin");
        File.WriteAllBytes(path, Content);
        return path;
    }

    private static int _sequence;

    private static string CreateFile(string name)
    {
        var path = Path.Combine(ScratchWorkspace.Root, name + "-" + RunTag + ".bin");
        File.WriteAllBytes(path, Content);
        return path;
    }

    private static IReadOnlyList<string> IndexRecords()
    {
        var bin = BinDirectory;
        return Directory.Exists(bin)
            ? [.. Directory.EnumerateFiles(bin, "$I*", SearchOption.TopDirectoryOnly)]
            : [];
    }

    private static string BinDirectory =>
        Path.Combine(ScratchWorkspace.VolumeRoot, "$RECYCLE.BIN", NativeBinLayout.CurrentSid);

    /// <summary>
    /// Makes every path this suite creates unique to this process, so two runs on one machine cannot put
    /// the same original path in the Recycle Bin twice.
    /// </summary>
    private static readonly string RunTag = $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks % 1_000_000}";

    private static readonly byte[] Content = new byte[4096];

    private static string Describe<TStage>(RecycleFailure<TStage>? failure)
        where TStage : struct, Enum =>
        failure is null ? "none" : $"{failure.Stage} 0x{failure.HResult:X8} {failure.Path}";
}
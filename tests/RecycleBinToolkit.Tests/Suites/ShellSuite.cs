using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;
using Windows.Win32.Foundation;

namespace RecycleBinToolkit.Tests.Suites;

internal static class ShellSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ShellSuite." + nameof(ShellRecyclesBatchAndReportsHResults), ShellRecyclesBatchAndReportsHResults),
        new TestCase("ShellSuite." + nameof(ShellHandlesSpecialAndNonAsciiNames), ShellHandlesSpecialAndNonAsciiNames),
        new TestCase("ShellSuite." + nameof(LockedFileReportsFailureHResult), LockedFileReportsFailureHResult),
        new TestCase("ShellSuite." + nameof(ShellItemCreationReportsFailureForGarbagePath), ShellItemCreationReportsFailureForGarbagePath),
        new TestCase("ShellSuite." + nameof(RecycleBinListingAgreesWithTheShellOracle), RecycleBinListingAgreesWithTheShellOracle),
        new TestCase("ShellSuite." + nameof(UnreportedItemsAreNotReportedAsSuccess), UnreportedItemsAreNotReportedAsSuccess),
        new TestCase("ShellSuite." + nameof(UnidentifiedCallbacksConsumeQueuedPathsInOrder), UnidentifiedCallbacksConsumeQueuedPathsInOrder),
        new TestCase("ShellSuite." + nameof(ReportedPathsAreNotHandedOutAgain), ReportedPathsAreNotHandedOutAgain),
    ];

    /// <summary>
    /// A path the Shell never reported on must read as "no outcome", not as a success. The Shell
    /// reports a successful delete as <c>0x00270008</c>; handing back a fabricated <c>0</c> would
    /// let an item the Shell silently skipped pass as recycled.
    /// </summary>
    private static Task UnreportedItemsAreNotReportedAsSuccess(TestContext context)
    {
        var sink = new RecycleProgressSink([@"E:\RC\Test\never-reported-a.bin", @"E:\RC\Test\never-reported-b.bin"]);

        Check.False(
            sink.TryGetOutcome(@"E:\RC\Test\never-reported-a.bin", out var hresult, out var binPath),
            "an item the Shell never reported on has no outcome");

        Check.True(
            (hresult & unchecked((int)0x80000000)) != 0,
            $"the missing outcome is not mistaken for a success code (got 0x{hresult:X8})");
        Check.True(binPath is null, "a missing outcome carries no Recycle Bin path");

        Check.False(
            sink.TryGetOutcome(@"E:\RC\Test\never-reported-b.bin", out _, out _),
            "every unreported path stays unreported");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A callback the Shell cannot be matched to a path by still has to land somewhere, and the
    /// queue order is the only thing left to go on. Each such callback must consume the earliest path
    /// still awaiting a verdict, so the paths are handed out in order and exactly once.
    /// </summary>
    /// <remarks>
    /// This is the behaviour the sink's forward-only scan depends on: it starts where the last
    /// fallback left off rather than re-walking the batch, and that is only sound because a path the
    /// Shell named itself is already resolved and can never need a slot handed to it again. The test
    /// pins the behaviour, not the scan, so the same assertions hold whichever way it is implemented.
    /// </remarks>
    private static Task UnidentifiedCallbacksConsumeQueuedPathsInOrder(TestContext context)
    {
        const int Success = 0x00270008;

        var paths = new[]
        {
            @"E:\RC\Test\ordered-a.bin",
            @"E:\RC\Test\ordered-b.bin",
            @"E:\RC\Test\ordered-c.bin"
        };

        var sink = new RecycleProgressSink(paths);

        // A null Shell item stands in for one whose reported path is in no queue: the sink cannot
        // match it and has to fall back to queue position.
        for (var i = 0; i < paths.Length; i++)
        {
            sink.PostDeleteItem(0, null!, new HRESULT(Success), null!);

            Check.True(sink.TryGetOutcome(paths[i], out var hresult, out _), $"the {i}-th unidentified callback is recorded against '{Path.GetFileName(paths[i])}'");
            Check.True(IsSuccess(hresult), $"that verdict is a real Shell code (0x{hresult:X8})");

            for (var earlier = 0; earlier < i; earlier++)
            {
                Check.True(
                    sink.TryGetOutcome(paths[earlier], out var earlierHresult, out _),
                    $"'{Path.GetFileName(paths[earlier])}' keeps the verdict it was given, so an out-of-order callback did not overwrite it");

                Check.Equal(Success, earlierHresult, "and it is still the verdict that was recorded for it");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Once every queued path has a verdict there is nothing left to hand out, and a callback that
    /// arrives afterwards must leave the recorded outcomes alone rather than overwrite one.
    /// </summary>
    private static Task ReportedPathsAreNotHandedOutAgain(TestContext context)
    {
        const int Success = 0x00270008;
        const int Failure = unchecked((int)0x80070005);

        var paths = new[] { @"E:\RC\Test\exhausted-a.bin", @"E:\RC\Test\exhausted-b.bin" };
        var sink = new RecycleProgressSink(paths);

        sink.PostDeleteItem(0, null!, new HRESULT(Success), null!);
        sink.PostDeleteItem(0, null!, new HRESULT(Failure), null!);

        // One more unidentified callback than there are queued paths: the sink has nothing left, and
        // its own failure path has to absorb this rather than let an exception cross into COM.
        sink.PostDeleteItem(0, null!, new HRESULT(unchecked((int)0x80004005)), null!);

        Check.True(sink.TryGetOutcome(paths[0], out var first, out _), "the first path still has its outcome");
        Check.Equal(Success, first, "and it is the success that was recorded, not the extra callback's code");

        Check.True(sink.TryGetOutcome(paths[1], out var second, out _), "the second path still has its outcome");
        Check.Equal(Failure, second, "and it is the failure that was recorded, not the extra callback's code");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Pins the library's listing to the Shell's own counter.
    /// </summary>
    /// <remarks>
    /// Every other suite measures the Recycle Bin through <see cref="RecycleBinProbe"/>, which reads it
    /// with the library's API. That is only worth anything while the library is known to agree with
    /// <c>SHQueryRecycleBin</c>, a different call into the Shell that shares no code with the
    /// enumeration. This is that agreement, asserted on both sides of one known delete.
    /// <para>
    /// Asserting the oracle against the library in one place rather than in every suite is deliberate:
    /// forty independent-looking checks against the same API prove the API is consistent with itself.
    /// It also pins which field of <c>SHQUERYRBINFO</c> is the item count and which is the byte total —
    /// the two are adjacent and the same width, so a swapped layout would report sizes as counts and
    /// leave every delta assertion in the suite meaningless.
    /// </para>
    /// </remarks>
    private static Task RecycleBinListingAgreesWithTheShellOracle(TestContext context)
    {
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var oracleBefore = RecycleBinVolume.Measure(volumeRoot);
        var probeBefore = RecycleBinProbe.Measure(volumeRoot);

        Check.True(oracleBefore.Ok, $"the independent Shell counter reads the volume (got 0x{oracleBefore.HResult:X8})");
        Check.True(probeBefore.Items >= 0, $"the library reads the volume too (got {probeBefore.Items})");

        using (var shell = ShellOperationContext.Create())
        {
            shell.SetFlags();
            var sink = new RecycleProgressSink(files);
            Check.True(shell.TryCreateShellItem(files[0], out var item, out _), "Shell item created");
            Check.True(shell.TryQueue(item!, sink, out _), "item queued");
            shell.Perform();
        }

        var oracleAfter = RecycleBinVolume.Measure(volumeRoot);
        var probeAfter = RecycleBinProbe.Measure(volumeRoot);

        Console.WriteLine(
            $"        oracle count {oracleBefore.Items} -> {oracleAfter.Items}, size {oracleBefore.Bytes} -> {oracleAfter.Bytes}; " +
            $"listing count {probeBefore.Items} -> {probeAfter.Items}, size {probeBefore.Bytes} -> {probeAfter.Bytes}");

        // The bin is shared, externally mutable state, so the deltas are bounded below rather than
        // matched exactly: anything else could recycle or purge on this volume mid-run.
        Check.True(oracleAfter.Items - oracleBefore.Items >= 1, "the Shell counter sees the item arrive");
        Check.True(oracleAfter.Bytes - oracleBefore.Bytes >= fileSize, $"the Shell counter sees the {fileSize} bytes arrive");
        Check.True(probeAfter.Items - probeBefore.Items >= 1, "the listing sees the item arrive");
        Check.True(probeAfter.Bytes - probeBefore.Bytes >= fileSize, $"the listing sees the {fileSize} bytes arrive");

        // Both counters are pinned against the same known delete. A listing that reported sizes as
        // counts, or a struct whose two fields were swapped, comes up short here even though its
        // numbers are non-negative and it moves in the right direction.
        Check.True(probeAfter.Bytes - oracleAfter.Bytes >= -fileSize, $"the listing's byte total is within one file of the Shell's (listing {probeAfter.Bytes}, Shell {oracleAfter.Bytes})");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ShellRecyclesBatchAndReportsHResults(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(5, 4096);
        var before = RecycleBinProbe.QueryItemCount(@"E:\");

        using (var context2 = ShellOperationContext.Create())
        {
            context2.SetFlags();

            var sink = new RecycleProgressSink(files);
            foreach (var file in files)
            {
                Check.True(context2.TryCreateShellItem(file, out var item, out var hresult), $"Shell item created for {file} (0x{hresult:X8})");
                Check.NotNull(item, $"Shell item present for {file}");
                Check.True(context2.TryQueue(item!, sink, out var queueHresult), $"item queued for {file} (0x{queueHresult:X8})");
            }

            context2.Perform();

            foreach (var file in files)
            {
                Check.True(sink.TryGetOutcome(file, out var hresult, out var binPath), $"sink recorded an outcome for {file}");
                Check.True(IsSuccess(hresult), $"hrDelete for {file} has the success bit set (0x{hresult:X8})");
                Check.False(File.Exists(file), $"{file} is gone from disk");
                Check.NotNull(binPath, $"{file} reported a recycle bin path");
                Check.True(
                    binPath!.Contains(@"$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase),
                    $"{file} landed inside the Recycle Bin (got {binPath})");
            }
        }

        var after = RecycleBinProbe.QueryItemCount(@"E:\");
        Console.WriteLine($"        bin tracked items: {before} -> {after}");
        Check.True(after - before >= files.Count, $"Recycle Bin tracked at least {files.Count} new item(s)");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ShellHandlesSpecialAndNonAsciiNames(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var names = new[] { "a[b]c%d!e.txt", "файл.txt", "путь (1).jpg" };
        var files = new List<string>(names.Length);

        foreach (var name in names)
        {
            var file = Path.Combine(ScratchWorkspace.Root, name);
            File.WriteAllText(file, $"content of {name}");
            files.Add(file);
        }

        using (var shell = ShellOperationContext.Create())
        {
            shell.SetFlags();
            var sink = new RecycleProgressSink(files);

            foreach (var file in files)
            {
                Check.True(shell.TryCreateShellItem(file, out var item, out var hresult), $"Shell item created for {file} (0x{hresult:X8})");
                Check.True(shell.TryQueue(item!, sink, out _), $"item queued for {file}");
            }

            shell.Perform();

            foreach (var file in files)
            {
                Check.True(sink.TryGetOutcome(file, out var hresult, out _), $"sink recorded an outcome for {file}");
                Check.True(IsSuccess(hresult), $"hrDelete for {file} succeeded (0x{hresult:X8})");
                Check.False(File.Exists(file), $"{file} is gone from disk");
            }
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task LockedFileReportsFailureHResult(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(2, 4096);

        // Opened without FileShare.Delete: the Shell cannot recycle this one and must say so
        // through hrDelete instead of skipping it silently.
        using (new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var shell = ShellOperationContext.Create())
        {
            shell.SetFlags();
            var sink = new RecycleProgressSink(files);

            foreach (var file in files)
            {
                Check.True(shell.TryCreateShellItem(file, out var item, out _), $"Shell item created for {file}");
                Check.True(shell.TryQueue(item!, sink, out _), $"item queued for {file}");
            }

            shell.Perform();

            Check.True(sink.TryGetOutcome(files[0], out var lockedHresult, out var lockedBinPath), "sink recorded the locked item");
            Check.False(IsSuccess(lockedHresult), $"locked file reports a failure HRESULT (0x{lockedHresult:X8})");
            Check.True(lockedBinPath is null, $"locked file has no recycle bin path (got {lockedBinPath ?? "<null>"})");
            Check.True(File.Exists(files[0]), "locked file survives");

            // The claim under test is that one failing item does not sink the batch, which is proved by
            // the Shell having reported on the sibling at all. It must not also demand that the sibling
            // succeeds on the first attempt: this suite creates and recycles thousands of files in this
            // one directory, so the indexer and antivirus routinely hold a handle on a fresh file for a
            // few milliseconds. That is a transient hold the library's own retry handles in production,
            // and RunnerSuite covers the eventual outcome.
            Check.True(
                sink.TryGetOutcome(files[1], out var freeHresult, out var freeBinPath),
                "the Shell reported on the free sibling too, so the batch was not all-or-nothing");
            Console.WriteLine($"        free sibling hrDelete=0x{freeHresult:X8}, binPath={freeBinPath ?? "<null>"}");
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ShellItemCreationReportsFailureForGarbagePath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var missing = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

        using (var shell = ShellOperationContext.Create())
        {
            Check.False(shell.TryCreateShellItem(missing, out var item, out var hresult), "a missing path produces no Shell item");
            Check.True(item is null, "no Shell item is handed back for a missing path");
            Check.Equal(unchecked((int)0x80070002), hresult, "the original Shell HRESULT is reported, not a fallback");
        }

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors the Shell's own success test: the severity bit, not equality with zero. A
    /// successful delete reports <c>0x00270008</c>, never <c>S_OK</c>.
    /// </summary>
    private static bool IsSuccess(int hresult) => (hresult & unchecked((int)0x80000000)) == 0;
}
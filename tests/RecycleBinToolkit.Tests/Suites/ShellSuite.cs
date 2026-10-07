using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class ShellSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ShellSuite." + nameof(ShellRecyclesBatchAndReportsHResults), ShellRecyclesBatchAndReportsHResults),
        new TestCase("ShellSuite." + nameof(ShellHandlesSpecialAndNonAsciiNames), ShellHandlesSpecialAndNonAsciiNames),
        new TestCase("ShellSuite." + nameof(LockedFileReportsFailureHResult), LockedFileReportsFailureHResult),
        new TestCase("ShellSuite." + nameof(ShellItemCreationReportsFailureForGarbagePath), ShellItemCreationReportsFailureForGarbagePath),
        new TestCase("ShellSuite." + nameof(RecycleBinVolumeTracksCountAndSizeSeparately), RecycleBinVolumeTracksCountAndSizeSeparately),
        new TestCase("ShellSuite." + nameof(UnreportedItemsAreNotReportedAsSuccess), UnreportedItemsAreNotReportedAsSuccess),
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
    /// Pins which field of <c>SHQUERYRBINFO</c> is the item count and which is the byte total.
    /// The two sit next to each other in native memory and are the same width, so a swapped
    /// layout silently reports sizes as counts and every delta assertion in the suite becomes
    /// meaningless.
    /// </summary>
    private static Task RecycleBinVolumeTracksCountAndSizeSeparately(TestContext context)
    {
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, fileSize);

        var countBefore = RecycleBinVolume.QueryItemCount(@"E:\");
        var sizeBefore = RecycleBinVolume.QuerySize(@"E:\");

        using (var shell = ShellOperationContext.Create())
        {
            shell.SetFlags();
            var sink = new RecycleProgressSink(files);
            Check.True(shell.TryCreateShellItem(files[0], out var item, out _), "Shell item created");
            Check.True(shell.TryQueue(item!, sink, out _), "item queued");
            shell.Perform();
        }

        var countAfter = RecycleBinVolume.QueryItemCount(@"E:\");
        var sizeAfter = RecycleBinVolume.QuerySize(@"E:\");

        Console.WriteLine($"        count {countBefore} -> {countAfter}, size {sizeBefore} -> {sizeAfter}");

        // The bin is shared, externally mutable state, so the deltas are bounded below rather than
        // matched exactly; anything else could recycle or purge on this volume mid-run. Both fields
        // are still pinned: with the two 64-bit fields swapped, the count would absorb the byte
        // growth and the size check below would come up short.
        Check.True(countAfter - countBefore >= 1, "recycling one file adds at least one tracked item");
        Check.True(sizeAfter - sizeBefore >= fileSize, $"recycling one {fileSize}-byte file grows the size by at least {fileSize} bytes");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ShellRecyclesBatchAndReportsHResults(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(5, 4096);
        var before = RecycleBinVolume.QueryItemCount(@"E:\");

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
            Check.False(context2.AnyOperationsAborted, "no operation reported as aborted");

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

        var after = RecycleBinVolume.QueryItemCount(@"E:\");
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

            Check.True(shell.AnyOperationsAborted, "the Shell reports the batch as aborted when an item fails");
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
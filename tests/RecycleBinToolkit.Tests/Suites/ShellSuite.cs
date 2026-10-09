using System.Reflection;
using System.Runtime.InteropServices;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;
using Windows.Win32.Foundation;

namespace RecycleBinToolkit.Tests.Suites;

internal static class ShellSuite
{
    /// <summary>
    /// How long a body on the Shell's own thread may take.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than the runner's own per-case budget, and deliberately a different number.
    /// Matching it is not the same as being safe: <see cref="StaThread.Run"/> bounds its wait with a
    /// join, and the runner's budget is a cancellation token handed to the case, which none of these
    /// bodies observe. A body that exceeds this is abandoned mid-Shell-call while the runner is still
    /// waiting on the join, so the shorter of the two is what fails fast and says why. fea4bae set
    /// this to the runner's ten minutes, which turned a two-minute failure into a ten-minute one across
    /// seven cases; five keeps the two distinguishable and the failure inside the outer one.
    /// </remarks>
    private static readonly TimeSpan CaseBudget = TimeSpan.FromMinutes(5);

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
        new TestCase("ShellSuite." + nameof(DisposedContextRefusesFurtherWork), DisposedContextRefusesFurtherWork),
        new TestCase("ShellSuite." + nameof(EveryShapeOfShellRefusalIsRecognised), EveryShapeOfShellRefusalIsRecognised),
        new TestCase("ShellSuite." + nameof(AContextThatLostItsOperationObjectSaysSoRatherThanFaulting), AContextThatLostItsOperationObjectSaysSoRatherThanFaulting),
    ];

    /// <summary>
    /// Runs a body that drives the Shell directly on a dedicated single-threaded apartment.
    /// </summary>
    /// <remarks>
    /// The rule <see cref="StaThread"/> states, enforced here rather than left to each case. The runner
    /// awaits every case with <c>ConfigureAwait(false)</c>, so a body runs on a thread-pool thread, which
    /// is MTA — and COM asked for STA on a thread already in another apartment answers
    /// <c>RPC_E_CHANGED_MODE</c>, which <c>ShellOperationContext.Create</c> swallows as "still usable".
    /// So a case that forgot this would run and pass in an apartment the library itself never uses, and
    /// nothing about its result would say so. The assertion inside is what keeps the wrapping honest
    /// rather than decorative.
    /// <para>
    /// The runner's own budget is named rather than left to the default. <see cref="StaThread"/>'s is two
    /// minutes, which is shorter than the runner's and would silently shorten every case moved onto this
    /// path by two thirds.
    /// </para>
    /// </remarks>
    private static void OnTheShellThread(Action body) =>
        StaThread.Run(
            () =>
            {
                Check.Equal(
                    ApartmentState.STA,
                    Thread.CurrentThread.GetApartmentState(),
                    "the body runs on a single-threaded apartment, which is what the Shell requires of every COM object it makes");

                body();
                return 0;
            },
            CaseBudget);

    /// <summary>
    /// Replacing the operation object drops the old one first, so a replacement that cannot be made
    /// leaves the context alive with nothing behind it. That state has to be reported as the Shell being
    /// unavailable rather than surfacing as a null reference from a property, because the context is
    /// shared by every operation in the process: one failure must not turn every later call into a
    /// NullReferenceException with a stack trace pointing inside this library.
    /// </summary>
    /// <remarks>
    /// The state is entered directly rather than by making COM fail, because a failing
    /// <c>CoCreateInstance</c> is not something a test can arrange. What is asserted is the invariant the
    /// state must satisfy, which is the part that was missing.
    /// </remarks>
    private static Task AContextThatLostItsOperationObjectSaysSoRatherThanFaulting(TestContext context)
    {
        OnTheShellThread(() =>
        {
            var shell = ShellOperationContext.Create();

            try
            {
                shell.SetFlags();

                // The state a failed replacement leaves behind: not disposed, no object, marked as failed.
                ClearOperationObject(shell);
                MarkOperationObjectFailed(shell);

                var thrown = Check.Throws<InvalidOperationException>(
                    () => shell.Perform(),
                    "an empty context reports the Shell being unavailable rather than faulting on a null reference");

                Check.NotNull(thrown.InnerException, "and says why the object went missing");
                Check.False(
                    thrown.Message.Contains("NullReference", StringComparison.OrdinalIgnoreCase),
                    "with nothing pointing at a null reference, which is what the caller would otherwise see");

                // Recreating from the emptied state has to be refused too, or a caller walks straight back
                // into it. Reflection wraps whatever the method throws, so the wrapper is unwrapped here.
                Check.Throws<InvalidOperationException>(
                    () => InvokeRecreate(shell),
                    "and recreating from the emptied state is refused rather than allowed to proceed");
            }
            finally
            {
                shell.Dispose();
            }
        });

        return Task.CompletedTask;
    }

    private static void InvokeRecreate(ShellOperationContext shell)
    {
        try
        {
            typeof(ShellOperationContext)
                .GetMethod("RecreateOperationObject", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(shell, null);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            throw wrapped.InnerException;
        }
    }

    private static void ClearOperationObject(ShellOperationContext shell) =>
        typeof(ShellOperationContext)
            .GetField("_fileOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell, null);

    private static void MarkOperationObjectFailed(ShellOperationContext shell) =>
        typeof(ShellOperationContext)
            .GetField("_failed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(shell, true);

    /// <summary>
    /// A Shell refusal reaches managed code as whatever exception the marshaller picks for its HRESULT,
    /// and it does not always pick the same one. Catching only <see cref="COMException"/> leaves most
    /// refusals to escape a path documented never to throw, taking a whole batch down over one file.
    /// </summary>
    /// <remarks>
    /// The list is the set measured on this build by handing the operation object calls it refuses. It
    /// pins the fact that the types vary rather than restating which ones are believed to occur, because
    /// the belief is what was wrong: <see cref="COMException"/> is the minority here.
    /// </remarks>
    private static Task EveryShapeOfShellRefusalIsRecognised(TestContext context)
    {
        // Measured: 0x8000FFFF and 0x80004005 give COMException, 0x80070057 ArgumentException,
        // 0x80070005 UnauthorizedAccessException, 0x8007000E OutOfMemoryException,
        // 0x80004001 NotImplementedException.
        foreach (var hr in new[]
                 {
                     unchecked((int)0x8000FFFF),
                     unchecked((int)0x80004005),
                     unchecked((int)0x80070057),
                     unchecked((int)0x80070005),
                     unchecked((int)0x8007000E),
                     unchecked((int)0x80004001),
                 })
        {
            Exception raised;
            try
            {
                Marshal.ThrowExceptionForHR(hr);
                throw new CheckFailedException($"0x{hr:X8} raised nothing, so there is no exception type to recognise");
            }
            catch (CheckFailedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                raised = ex;
            }

            Check.True(
                ShellOperationContext.IsShellRefusal(raised),
                $"a Shell refusal reported as {raised.GetType().Name} (0x{hr:X8}) is recognised as one");
        }

        Check.False(
            ShellOperationContext.IsShellRefusal(new ObjectDisposedException("context")),
            "a disposed context is a defect in how it is used, not the Shell declining, and is not recorded as an item failure");

        Check.False(
            ShellOperationContext.IsShellRefusal(new InvalidOperationException("bug")),
            "an ordinary logic fault is not mistaken for a Shell refusal");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposing must release the operation object, and a released context must say so rather than
    /// carrying on with it.
    /// </summary>
    /// <remarks>
    /// The apartment is torn down in the same call, so an object still alive when that happens is
    /// released later, on whichever thread the finalizer happens to run. For something that must be
    /// released on the thread that created it, that is a leak rather than a cleanup, so the object is
    /// released first and deterministically.
    /// <para>
    /// Every member checks the context, because releasing the object without noticing would leave the
    /// context able to hand out an object it no longer owns. The check belongs in one place for that to
    /// be true of all of them.
    /// </para>
    /// </remarks>
    private static Task DisposedContextRefusesFurtherWork(TestContext context)
    {
        OnTheShellThread(() =>
        {
            var shell = ShellOperationContext.Create();

            shell.SetFlags();
            shell.Dispose();

            Check.Throws<ObjectDisposedException>(
                () => shell.SetFlags(),
                "applying flags to a disposed context is refused rather than using a released object");
            Check.Throws<ObjectDisposedException>(
                () => shell.Perform(),
                "performing through a disposed context is refused");

            shell.Dispose();
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// A path the Shell never reported on must read as "no outcome", not as a success. The Shell
    /// reports a successful delete as <c>0x00270008</c>; handing back a fabricated <c>0</c> would
    /// let an item the Shell silently skipped pass as recycled.
    /// </summary>
    private static Task UnreportedItemsAreNotReportedAsSuccess(TestContext context)
    {
        var sink = new RecycleProgressSink([@"W:\work\never-reported-a.bin", @"W:\work\never-reported-b.bin"]);

        Check.False(
            sink.TryGetOutcome(@"W:\work\never-reported-a.bin", out var hresult, out var binPath),
            "an item the Shell never reported on has no outcome");

        Check.True(
            (hresult & unchecked((int)0x80000000)) != 0,
            $"the missing outcome is not mistaken for a success code (got 0x{hresult:X8})");
        Check.True(binPath is null, "a missing outcome carries no Recycle Bin path");

        Check.False(
            sink.TryGetOutcome(@"W:\work\never-reported-b.bin", out _, out _),
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
            @"W:\work\ordered-a.bin",
            @"W:\work\ordered-b.bin",
            @"W:\work\ordered-c.bin"
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

        var paths = new[] { @"W:\work\exhausted-a.bin", @"W:\work\exhausted-b.bin" };
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
    /// Pins both of the library's views of the Recycle Bin to the Shell's own counter.
    /// </summary>
    /// <remarks>
    /// There are two views, and they are checked against <c>SHQueryRecycleBin</c> separately because
    /// they reach the Shell by different routes and can disagree independently.
    /// <para>
    /// The counter is what the rest of the suites read. <see cref="RecycleBinProbe"/> asks the library's
    /// own <c>RecycleBinCounter</c>, which is this same Shell call wrapped — so on its own it proves the
    /// struct is laid out correctly and nothing more. <see cref="RecycleBinVolume"/> is the independent
    /// version of the same call, differing only in where it declares the two 64-bit fields, and it is what
    /// catches a layout mistake.
    /// </para>
    /// <para>
    /// The listing is the other view, and it is the one that actually enumerates: six COM round trips and
    /// a stat per entry, through a path that shares no code with the counter. Nothing else in the suite
    /// compares it to anything, so if the two ever stopped agreeing the delta assertions elsewhere would
    /// quietly stop meaning what they claim. That comparison is what this test is for.
    /// </para>
    /// <para>
    /// Asserting the agreement in one place rather than in every suite is deliberate: forty
    /// independent-looking checks against the same API prove the API is consistent with itself.
    /// </para>
    /// </remarks>
    private static async Task RecycleBinListingAgreesWithTheShellOracle(TestContext context)
    {
        const int fileSize = 4096;

        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for both counters and the listing to read the same volume");
        var files = workspace.CreateRandomFiles(1, fileSize);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var oracleBefore = RecycleBinVolume.Measure(volumeRoot);
        var probeBefore = RecycleBinProbe.Measure(volumeRoot);
        var listingBefore = await RecycleBin.GetItemsAsync(volumeRoot, null, context.CancellationToken).ConfigureAwait(false);

        Check.True(oracleBefore.Ok, $"the independent Shell counter reads the volume (got 0x{oracleBefore.HResult:X8})");
        Check.True(probeBefore.Items >= 0, $"the library reads the volume too (got {probeBefore.Items})");
        Check.True(oracleBefore.Items == probeBefore.Items, $"the two counters agree before the delete (Shell {oracleBefore.Items}, library {probeBefore.Items})");

        OnTheShellThread(() =>
        {
            using var shell = ShellOperationContext.Create();
            shell.SetFlags();
            var sink = new RecycleProgressSink(files);
            Check.True(shell.TryCreateShellItem(files[0], out var item, out _), "Shell item created");
            Check.True(shell.TryQueue(item!, sink, out _), "item queued");
            shell.Perform();
        });

        var oracleAfter = RecycleBinVolume.Measure(volumeRoot);
        var probeAfter = RecycleBinProbe.Measure(volumeRoot);
        var listingAfter = await RecycleBin.GetItemsAsync(volumeRoot, null, context.CancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"        counter {oracleBefore.Items} -> {oracleAfter.Items}, size {oracleBefore.Bytes} -> {oracleAfter.Bytes}; " +
            $"listing {listingBefore.Items.Count} -> {listingAfter.Items.Count}, size {listingBefore.TotalSize} -> {listingAfter.TotalSize}");

        // The bin is shared, externally mutable state, so the deltas are bounded below rather than
        // matched exactly: anything else could recycle or purge on this volume mid-run.
        Check.True(oracleAfter.Items - oracleBefore.Items >= 1, "the Shell counter sees the item arrive");
        Check.True(oracleAfter.Bytes - oracleBefore.Bytes >= fileSize, $"the Shell counter sees the {fileSize} bytes arrive");
        Check.True(probeAfter.Items - probeBefore.Items >= 1, "the library's counter sees the item arrive");
        Check.True(probeAfter.Bytes - probeBefore.Bytes >= fileSize, $"the library's counter sees the {fileSize} bytes arrive");

        // The listing is the only view that enumerates, and nothing else compares it to anything. Both
        // halves of it are checked against the Shell's counters: a listing that reported sizes as
        // counts, or dropped an entry, would come up short here while still moving in the right
        // direction and never reporting a negative number.
        Check.True(
            listingAfter.Items.Count - listingBefore.Items.Count >= 1,
            $"the listing sees the item arrive (listing {listingBefore.Items.Count} -> {listingAfter.Items.Count})");
        Check.True(
            listingAfter.TotalSize - listingBefore.TotalSize >= fileSize,
            $"the listing sees the {fileSize} bytes arrive (listing {listingBefore.TotalSize} -> {listingAfter.TotalSize})");
        Check.True(
            listingAfter.TotalSize <= oracleAfter.Bytes,
            $"the listing does not claim more bytes than the Shell counted (listing {listingAfter.TotalSize}, Shell {oracleAfter.Bytes})");

        workspace.Dispose();
    }

    private static Task ShellRecyclesBatchAndReportsHResults(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the five items to land in and report a bin path for");
        var files = workspace.CreateRandomFiles(5, 4096);
        var (before, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");

        OnTheShellThread(() =>
        {
            using var context2 = ShellOperationContext.Create();
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
        });

        var (after, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the count after the batch");
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

        OnTheShellThread(() =>
        {
            using var shell = ShellOperationContext.Create();
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
        });

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task LockedFileReportsFailureHResult(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(2, 4096);

        // Opened without FileShare.Delete: the Shell cannot recycle this one and must say so
        // through hrDelete instead of skipping it silently. The handle is taken inside the STA body so
        // it is released on that thread too, rather than from whichever thread disposes it.
        OnTheShellThread(() =>
        {
            using var held = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            using var shell = ShellOperationContext.Create();
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
        });

        workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Task ShellItemCreationReportsFailureForGarbagePath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var missing = Path.Combine(ScratchWorkspace.Root, "definitely-absent.bin");

        OnTheShellThread(() =>
        {
            using var shell = ShellOperationContext.Create();
            Check.False(shell.TryCreateShellItem(missing, out var item, out var hresult), "a missing path produces no Shell item");
            Check.True(item is null, "no Shell item is handed back for a missing path");
            Check.Equal(unchecked((int)0x80070002), hresult, "the original Shell HRESULT is reported, not a fallback");
        });

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors the Shell's own success test: the severity bit, not equality with zero. A
    /// successful delete reports <c>0x00270008</c>, never <c>S_OK</c>.
    /// </summary>
    private static bool IsSuccess(int hresult) => (hresult & unchecked((int)0x80000000)) == 0;
}
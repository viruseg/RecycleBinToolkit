// Every method below is, verbatim, the body of a code block in README.md. README.md is the
// documentation; this file is the copy that compiles, which is how "the example builds" stays true.
// Change an example and both files change in the same edit — see AGENTS.md at the repository root.
//
// Not registered as a test, and nothing here is ever run: it exists only to prove the documented API
// shapes compile as written. Call it and you delete your own files.
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Docs;

internal static class ReadmeExamples
{
    internal static async Task QuickStart()
    {
        await FileRecycler.MoveToRecycleBinAsync(@"W:\Users\me\Downloads\archive.zip");
    }

    internal static async Task QuickStartWithTheBinItself()
    {
        await FileRecycler.MoveToRecycleBinAsync(@"W:\Users\me\Downloads\archive.zip");

        await RecycleBin.GetItemsAsync();
        await RecycleBin.EmptyAsync(@"W:\");
    }

    internal static async Task BatchReportingFailures()
    {
        // TopDirectoryOnly on purpose: a whole tree is one item, and the batch overload is for many
        // paths the caller already has.
        var paths = Directory.GetFiles(@"W:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

        // This overload never throws for a per-item failure — a bad path lands in result.Failures
        // and the rest of the batch still runs.
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        if (!result.Succeeded)
        {
            foreach (var failure in result.Failures)
            {
                Console.Error.WriteLine(
                    $"{failure.Path}: {failure.Stage} (0x{failure.HResult:X8}) after {failure.Attempts} attempt(s)");
            }
        }
    }

    internal static async Task ServiceNeverThrows()
    {
        IReadOnlyList<string?> paths = [];
        var options = new RecycleBinOptions { ThrowOnError = false };

        // An empty list is a valid call: it succeeds and does nothing. Entries may also be null or
        // missing, each reported as a failure without aborting the batch.
        var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

        if (!result.Succeeded)
        {
            foreach (var failure in result.Failures)
            {
                Console.Error.WriteLine($"recycle failed for {failure.Path}: {failure.Stage} 0x{failure.HResult:X8}");
            }
        }
    }

    internal static void FullOptions()
    {
        // Every property has a production default, so RecycleBinOptions.Default — which is what a null
        // options argument means — is always a valid argument.
        var options = new RecycleBinOptions
        {
            MaxAttempts = 10,
            InitialRetryDelay = TimeSpan.FromMilliseconds(100),
            OverallTimeout = TimeSpan.FromSeconds(5),
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
        };

        // ThrowOnError is the global switch: with it off a refusal arrives in Failures instead of raising.
        Console.WriteLine(options.OverallTimeout);
    }

    internal static void ReadHResult()
    {
        var failure = new RecycleFailure(@"W:\x.bin", unchecked((int)0x80270027), 1, RecycleFailureStage.Performing);

        // Success and failure are told apart by the severity bit rather than by comparing with zero.
        bool moveSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
        Console.WriteLine(moveSucceeded);
    }

    internal static void ResultMembers()
    {
        // Create exists so a test can hold a result without touching the file system. In application
        // code the result comes back from a call.
        var result = RecycleBinResult.Create(["a"], [], [], TimeSpan.Zero);

        // Succeeded means gone from disk, not necessarily recycled: a file removed permanently under an
        // explicit policy counts as a success. Check PermanentlyDeletedPaths when that matters.
        Console.WriteLine($"{result.Succeeded} {result.Elapsed} {result.FirstFailure is null}");
    }

    internal static async Task RecycleOneFile()
    {
        // The library writes the Recycle Bin's own files under <volume>$RECYCLE.BIN\<SID>\ and reads them back
        // with ordinary file calls. It needs Windows 10 version 1607 (build 14393) or later, because that is the
        // first build that reads the index records it writes; below that the call raises
        // PlatformNotSupportedException, and ThrowOnError does not suppress that.
        await FileRecycler.MoveToRecycleBinAsync(@"W:\Users\me\Downloads\archive.zip");

        var contents = await RecycleBin.GetItemsAsync(@"W:\");
        await RecycleBin.EmptyAsync(@"W:\");
    }

    internal static async Task ListTheRecycleBin()
    {
        var contents = await RecycleBin.GetItemsAsync();

        foreach (var item in contents.Items)
        {
            // Size is 0 for a directory — its contents cannot be enumerated without elevation, so
            // IsDirectory is the honest signal that no figure is available.
            //
            // OriginalPath and DeletedAt are read from the entry's record on first access, which is I/O
            // and can raise IOException if the record was collected meanwhile. The two come out of one
            // reading, so asking for either gives you both.
            Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
        }

        // TotalSize is derived from Items when the listing is built, so it cannot disagree with them.
        Console.WriteLine($"{contents.Items.Count} item(s), {contents.TotalSize} byte(s)");
    }

    internal static async Task ListTheRecycleBinAndReadEveryRecordUpFront()
    {
        // A listing reads no $I records by default, which is what makes one walk of the bin directory
        // enough. A caller that is going to read every entry's path anyway can have the records read during
        // the listing instead of one at a time from its own loop — and an entry whose record cannot be read
        // is reported in Failures rather than raising later.
        var eager = new RecycleBinOptions { PreloadItemDetails = true };

        var contents = await RecycleBin.GetItemsAsync(@"W:\", eager);

        foreach (var item in contents.Items)
        {
            Console.WriteLine($"{item.OriginalPath} — {item.Size} byte(s)");
        }

        foreach (var failure in contents.Failures)
        {
            Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task LoadEveryItemsDetailsBeforeLoopingOverThem()
    {
        var contents = await RecycleBin.GetItemsAsync();

        // Read every item's OriginalPath and DeletedAt up front, in parallel, so the loop below never blocks on I/O.
        contents.LoadItemDetails();

        foreach (var item in contents.Items)
        {
            Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
        }
    }

    internal static async Task ListOneVolumeAndHearAboutUnreadableEntries()
    {
        // Each volume's bin is enumerated directly, so this reads exactly the volume named.
        var onW = await RecycleBin.GetItemsAsync(@"W:\");

        foreach (var failure in onW.Failures)
        {
            Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task EmptyAndReportWhatItFreed()
    {
        var emptied = await RecycleBin.EmptyAsync(@"W:\");

        // Both figures are summed from the records being deleted. Neither is ever negative.
        Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

        foreach (var failure in emptied.Failures)
        {
            Console.Error.WriteLine($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task ActOnAVolumeWhoseRecycleBinIsOff()
    {
        // A volume root, not a directory inside one.
        var status = await RecycleBin.GetStatusAsync(@"W:\");

        if (!status.IsEnabled)
        {
            Console.Error.WriteLine($"the Recycle Bin is not in use on {status.VolumeRoot}: {status.Reason}");

            // The reasons differ in who can fix them, which is why this is not a bool. Indeterminate
            // is deliberately not Enabled: the library does not assert what it could not read.
            if (status.Reason is RecycleBinAvailabilityReason.DisabledByPolicy)
            {
                Console.Error.WriteLine("an administrator set this; nothing in the application can change it");
            }
        }
    }

    internal static async Task RecycleDirectory()
    {
        // A directory tree is one path. The single-path overload returns a bool and no reason, so the
        // batch overload with one element is the way to learn why it failed.
        IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        if (result.Succeeded)
        {
            Console.WriteLine("recycled");
        }
        else
        {
            foreach (var failure in result.Failures)
            {
                Console.Error.WriteLine($"{failure.Path} — {failure.Stage}, 0x{failure.HResult:X8}");
            }
        }
    }

    internal static async Task ReportEveryFailure()
    {
        // A path that is not there comes back as a failure and does not stop the other entries.
        IReadOnlyList<string?> paths = [@"W:\Projects\build-output", @"W:\Projects\gone.log"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine(
                $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
        }
    }

    internal static async Task LongPathsIntoTheBin()
    {
        // A path hundreds of characters long recycles with no staging, no policy and no limit: the
        // destination inside the bin is short whatever the source was.
        IReadOnlyList<string?> paths = [@"W:\Projects\a-very-long-path\build-output"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        Console.WriteLine($"{result.RecycledPaths.Count} path(s) recycled");
    }

    internal static async Task OptOutOfARecycleBinRefusal()
    {
        IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];
        var options = new RecycleBinOptions
        {
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
        };

        var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

        // Only paths on volumes that cannot recycle are affected; everything else still goes to the bin.
        if (result.PermanentlyDeletedPaths.Count > 0)
        {
            Console.Error.WriteLine($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
        }
    }

    internal static async Task CancelTheWait()
    {
        IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Cancelling stops the batch between paths: a cancelled call stops part way and raises without
        // returning a result, so which files went is found by reading the bin.
        await FileRecycler.MoveToRecycleBinAsync(paths, cancellationToken: cts.Token);
    }

    internal static async Task RefuseABatchOnAVolumeWithoutARecycleBin()
    {
        IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];
        var options = new RecycleBinOptions { ThrowOnError = false };

        // The default NoRecycleBinAction is Throw: on a volume with no Recycle Bin the path is refused
        // before anything is attempted. With ThrowOnError off the refusal comes back as data instead of
        // an exception.
        var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine($"{failure.Path} was not recycled: {failure.Stage} 0x{failure.HResult:X8}");
        }
    }

    // The one documented exception to "README.md is a verbatim copy" is the assertion helper, and it is
    // spelled the same way in both files: README.md uses Check.* too, not Assert.*. Both files carry the
    // note that a project with xunit spells the same three lines Assert.True / Assert.Equal /
    // Assert.False. Everything else here is character for character.
    internal static async Task AssertInATest()
    {
        IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        Check.True(result.Succeeded, "the batch succeeded");
        Check.Equal(0, result.Failures.Count, "no failures");

        // Succeeded is derived from the library's own checks; File.Exists is the independent one.
        foreach (var path in result.RecycledPaths)
        {
            Check.False(File.Exists(path), $"{path} is gone");
        }
    }
}

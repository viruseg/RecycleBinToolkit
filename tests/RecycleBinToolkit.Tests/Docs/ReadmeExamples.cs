// Every method below is, verbatim, the body of a code block in README.md. README.md is the
// documentation; this file is the copy that compiles, which is how "the example builds" stays true.
// Change an example and both files change in the same edit — see AGENTS.md at the repository root.
//
// Not registered as a test, and nothing here is ever run: it exists only to prove the documented API
// shapes compile as written. Call it and you delete your own files.
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Docs;

internal static class ReadmeExamples
{
    internal static async Task QuickStartWithTheBinItself()
    {
        await RecycleBin.RecycleAsync(@"W:\Users\me\Downloads\archive.zip");

        await RecycleBin.GetItemsAsync();
        await RecycleBin.EmptyAsync(@"W:\");
    }

    internal static async Task QuickStart()
    {
        await RecycleBin.RecycleAsync(@"W:\Users\me\Downloads\archive.zip");
    }

    internal static async Task BatchReportingFailures()
    {
        var paths = Directory.GetFiles(@"W:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

        var result = await RecycleBin.RecycleAsync(paths);

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

        var result = await RecycleBin.RecycleAsync(paths);

        if (!result.Succeeded)
        {
            foreach (var failure in result.Failures)
            {
                Console.Error.WriteLine($"recycle failed for {failure.Path}: {failure.Stage} 0x{failure.HResult:X8}");
            }
        }
    }

    internal static void RecycleOptionsExample()
    {
        var options = new RecycleOptions
        {
            MaxAttempts = 10,
            InitialRetryDelay = TimeSpan.FromMilliseconds(100),
            OverallTimeout = TimeSpan.FromSeconds(5),
            ThrowOnError = false,
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently
        };

        Console.WriteLine(options.OverallTimeout);
    }

    internal static void RestoreOptionsExample()
    {
        var options = new RestoreOptions
        {
            MaxAttempts = 10,
            InitialRetryDelay = TimeSpan.FromMilliseconds(100),
            OverallTimeout = TimeSpan.FromSeconds(5),
            FileConflict = RestoreFileConflict.Rename,
            DirectoryConflict = RestoreDirectoryConflict.RenameDirectory
        };

        Console.WriteLine(options.OverallTimeout);
    }

    internal static void GetItemsOptionsExample()
    {
        var options = new GetItemsOptions { PreloadItemDetails = true };

        Console.WriteLine(options.PreloadItemDetails);
    }

    internal static void ResultMembers()
    {
        var result = RecycleBinResult.Create(["a"], [], [], TimeSpan.Zero);

        Console.WriteLine($"{result.Succeeded} {result.Elapsed} {result.FirstFailure is null}");
    }

    internal static void ReadHResult()
    {
        var failure = new RecycleFailure<RecyclePathFailureStage>(@"W:\x.bin", unchecked((int)0x80270027), 1, RecyclePathFailureStage.Performing);

        bool moveSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
        Console.WriteLine(moveSucceeded);
    }

    internal static async Task ListTheRecycleBin()
    {
        var contents = await RecycleBin.GetItemsAsync();

        foreach (var volume in contents.Items)
        {
            Console.WriteLine($"{volume.VolumeRoot} — {volume.Items.Count} item(s)");

            foreach (var item in volume.Items)
            {
                Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
            }
        }

        foreach (var failure in contents.Failures)
        {
            Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
        }

        Console.WriteLine($"{contents.TotalSize} byte(s) in total");
    }

    internal static async Task LoadEveryItemsDetailsBeforeLoopingOverThem()
    {
        var contents = await RecycleBin.GetItemsAsync();

        contents.LoadItemDetails();

        foreach (var volume in contents.Items)
        {
            foreach (var item in volume.Items)
            {
                Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
            }
        }
    }

    internal static async Task ListOneVolumeAndReadEveryRecordUpFront()
    {
        var eager = new GetItemsOptions { PreloadItemDetails = true };

        var contents = await RecycleBin.GetItemsAsync(@"W:\", eager);

        foreach (var volume in contents.Items)
        {
            foreach (var item in volume.Items)
            {
                Console.WriteLine($"{item.OriginalPath} — {item.Size} byte(s)");
            }
        }

        foreach (var failure in contents.Failures)
        {
            Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task EmptyAndReportWhatItFreed()
    {
        var emptied = await RecycleBin.EmptyAsync(@"W:\");

        Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

        foreach (var failure in emptied.Failures)
        {
            Console.Error.WriteLine($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task RestoreOneItemAndReportWhatWasInItsWay()
    {
        var contents = await RecycleBin.GetItemsAsync(@"W:\");

        var entry = contents.Items[0].Items[0];

        var options = new RestoreOptions { FileConflict = RestoreFileConflict.Rename };

        var result = await RecycleBin.RestoreAsync([entry], options);

        Console.WriteLine($"{result.ItemsRestored} item(s), {result.RestoredSize} byte(s), in {result.Elapsed.TotalMilliseconds:F0} ms");

        foreach (var path in result.RestoredPaths)
        {
            Console.WriteLine($"restored: {path}");
        }

        foreach (var conflict in result.Conflicts)
        {
            Console.WriteLine($"conflict: {conflict.Path}, {conflict.Action}");
        }

        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine($"failed: {failure.Path}, {failure.Stage}, 0x{failure.HResult:X8}");
        }
    }

    internal static async Task ActOnAVolumeWhoseRecycleBinIsOff()
    {
        var status = await RecycleBin.GetStatusAsync(@"W:\");

        if (!status.IsEnabled)
        {
            Console.Error.WriteLine($"the Recycle Bin is not in use on {status.VolumeRoot}: {status.Reason}");

            if (status.Reason is RecycleBinAvailabilityReason.DisabledByPolicy)
            {
                Console.Error.WriteLine("an administrator set this; nothing in the application can change it");
            }
        }
    }

    internal static async Task RecycleABatchAndReportEveryFailure()
    {
        IReadOnlyList<string?> paths =
        [
            @"W:\Projects\build-output",
            @"W:\Projects\a-very-long-path\dist",
            @"W:\Projects\gone.log",
        ];

        var result = await RecycleBin.RecycleAsync(paths);

        Console.WriteLine($"{result.RecycledPaths.Count} recycled, {result.PermanentlyDeletedPaths.Count} deleted");

        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine(
                $"{failure.Path} — {failure.Stage}, 0x{failure.HResult:X8}, attempts={failure.Attempts}");
        }
    }

    internal static async Task ChooseWhatHappensOnAVolumeWithoutARecycleBin()
    {
        IReadOnlyList<string?> paths = [@"W:\Reports\quarterly.xlsx"];

        var refused = await RecycleBin.RecycleAsync(paths);

        foreach (var failure in refused.Failures)
        {
            Console.Error.WriteLine($"{failure.Path} was not recycled: {failure.Stage} 0x{failure.HResult:X8}");
        }

        var options = new RecycleOptions { NoRecycleBinAction = NoRecycleBinAction.DeletePermanently };
        var deleted = await RecycleBin.RecycleAsync(paths, options);

        Console.WriteLine($"{deleted.PermanentlyDeletedPaths.Count} file(s) deleted, not recycled");
    }

    internal static async Task CancelTheWait()
    {
        IReadOnlyList<string?> paths = [@"W:\Projects\build-output"];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await RecycleBin.RecycleAsync(paths, cancellationToken: cts.Token);
    }

    internal static async Task SinglePathWithoutThrowing()
    {
        var options = new RecycleOptions { ThrowOnError = false };

        bool gone = await RecycleBin.RecycleAsync(@"W:\Reports\quarterly.xlsx", options);
    }

    internal static async Task ReportEachPathAsItIsRecycled()
    {
        var options = new RecycleOptions
        {
            OnItemProcessed = progress =>
                Console.WriteLine($"{progress.Index}/{progress.Count} {progress.Status} {progress.Path}"),
            WaitForProgressCallbacks = true
        };

        await RecycleBin.RecycleAsync([@"W:\Users\me\Downloads\archive.zip"], options);
    }

    internal static async Task ReportEachEntryAsTheBinIsEmptied()
    {
        await RecycleBin.EmptyAsync(@"W:\", new EmptyOptions
        {
            OnItemProcessed = progress => Console.WriteLine($"{progress.Index}/{progress.Count} {progress.Status}")
        });
    }
}

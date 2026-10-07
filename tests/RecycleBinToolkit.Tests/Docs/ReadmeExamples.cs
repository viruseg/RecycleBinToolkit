// Compile check for the README examples. Not registered as a test: it exists only to prove the
// documented API shapes actually compile as written.
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Docs;

internal static class ReadmeExamples
{
    internal static async Task QuickStart()
    {
        await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");
    }

    internal static async Task QuickStartWithTheBinItself()
    {
        await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");

        await RecycleBin.GetItemsAsync();
        await RecycleBin.EmptyAsync(@"E:\");
    }

    internal static async Task BatchReportingFailures()
    {
        var paths = Directory.GetFiles(@"C:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        if (!result.Succeeded)
        {
            foreach (var failure in result.Failures)
            {
                await Console.Error.WriteLineAsync(
                    $"{failure.Path}: {failure.Stage} (0x{failure.HResult:X8}) after {failure.Attempts} attempt(s)");
            }
        }
    }

    internal static async Task ServiceNeverThrows()
    {
        IReadOnlyList<string?> paths = [];
        var options = new RecycleBinOptions { ThrowOnError = false };
        var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

        if (!result.Succeeded)
        {
            Console.WriteLine(result.Failures.Count);
        }
    }

    internal static async Task RecycleDirectory()
    {
        IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);
        Console.WriteLine(result.Succeeded ? "recycled" : $"{result.Failures.Count} failed");
    }

    internal static async Task ReportEveryFailure()
    {
        var result = await FileRecycler.MoveToRecycleBinAsync([]);

        foreach (var failure in result.Failures)
        {
            await Console.Error.WriteLineAsync(
                $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
        }
    }

    internal static async Task LongPathsIntoTheBin()
    {
        var options = new RecycleBinOptions
        {
            LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
        };

        var result = await FileRecycler.MoveToRecycleBinAsync([], options);
        Console.WriteLine(result.LeftoverStagingDirectory);
    }

    internal static async Task OptInToPermanentDeletion()
    {
        var options = new RecycleBinOptions
        {
            LongPathFailureAction = LongPathFailureAction.DeletePermanently
        };

        var result = await FileRecycler.MoveToRecycleBinAsync([], options);

        if (result.PermanentlyDeletedPaths.Count > 0)
        {
            await Console.Error.WriteLineAsync($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
        }
    }

    internal static async Task CancelTheWait()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FileRecycler.MoveToRecycleBinAsync([], cancellationToken: cts.Token);
    }

    internal static async Task AssertInATest()
    {
        var result = await FileRecycler.MoveToRecycleBinAsync([]);

        Check.True(result.Succeeded, "the batch succeeded");
        Check.Equal(0, result.Failures.Count, "no failures");

        foreach (var path in result.RecycledPaths)
        {
            Check.False(File.Exists(path), $"{path} is gone");
        }
    }

    internal static void ReadHResult()
    {
        var failure = new RecycleFailure(@"C:\x.bin", unchecked((int)0x80270027), 1, RecycleFailureStage.Performing);
        bool shellSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
        Console.WriteLine(shellSucceeded);
    }

    internal static void FullOptions()
    {
        var options = new RecycleBinOptions
        {
            MaxAttempts = 10,
            InitialRetryDelay = TimeSpan.FromMilliseconds(100),
            OverallTimeout = TimeSpan.FromSeconds(5),
            ThrowOnError = false,
            LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
        };

        Console.WriteLine(options.MaxShellPathLength);
    }

    internal static void ResultMembers()
    {
        var result = RecycleBinResult.Create(["a"], [], [], TimeSpan.Zero);
        Console.WriteLine($"{result.Succeeded} {result.Elapsed} {result.FirstFailure is null}");
    }

    internal static async Task ListTheRecycleBin()
    {
        var contents = await RecycleBin.GetItemsAsync();

        foreach (var item in contents.Items)
        {
            Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
        }

        Console.WriteLine($"{contents.Items.Count} item(s), {contents.TotalSize} byte(s)");
    }

    internal static async Task ListOneVolumeAndHearAboutUnreadableEntries()
    {
        var onE = await RecycleBin.GetItemsAsync(@"E:\");

        foreach (var failure in onE.Failures)
        {
            await Console.Error.WriteLineAsync($"could not read an entry: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task EmptyAndReportWhatItFreed()
    {
        var emptied = await RecycleBin.EmptyAsync(@"E:\");

        Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

        foreach (var failure in emptied.Failures)
        {
            await Console.Error.WriteLineAsync($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task ActOnAVolumeWhoseRecycleBinIsOff()
    {
        var status = await RecycleBin.GetStatusAsync(@"E:\");

        if (!status.IsEnabled)
        {
            await Console.Error.WriteLineAsync($"the Recycle Bin is not in use on {status.VolumeRoot}: {status.Reason}");

            if (status.Reason is RecycleBinAvailabilityReason.DisabledByPolicy)
            {
                await Console.Error.WriteLineAsync("an administrator set this; nothing in the application can change it");
            }
        }
    }
}
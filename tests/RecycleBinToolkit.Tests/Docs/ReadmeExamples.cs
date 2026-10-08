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
        await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");
    }

    internal static async Task QuickStartWithTheBinItself()
    {
        await FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip");

        await RecycleBin.GetItemsAsync();
        await RecycleBin.EmptyAsync(@"X:\");
    }

    internal static async Task BatchReportingFailures()
    {
        // TopDirectoryOnly on purpose: a whole tree is one item, and the batch overload is for many
        // paths the caller already has.
        var paths = Directory.GetFiles(@"C:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);

        // This overload never throws for a per-item failure — a bad path lands in result.Failures
        // and the rest of the batch still runs.
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        if (!result.Succeeded)
        {
            foreach (var failure in result.Failures)
            {
                // Attempts is 0 when the item never reached the Shell, so the HRESULT is 0 too.
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
            LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
        };

        // ThrowOnError is the global switch and has the final say over LongPathFailureAction.
        // 251 recycles and 261 does not, measured on the machine this was developed against.
        Console.WriteLine(options.MaxShellPathLength);
    }

    internal static void ReadHResult()
    {
        var failure = new RecycleFailure(@"C:\x.bin", unchecked((int)0x80270027), 1, RecycleFailureStage.Performing);

        // The Shell reports a successful delete as 0x00270008, never as S_OK, so success and failure
        // are told apart by the severity bit rather than by comparing with zero. On its own that is
        // not enough: an item can carry a success code and still be a failure, which is why Stage
        // is read too.
        bool shellSucceeded = (failure.HResult & unchecked((int)0x80000000)) == 0;
        Console.WriteLine(shellSucceeded);
    }

    internal static void ResultMembers()
    {
        // Create exists so a test can hold a result without touching the file system. In application
        // code the result comes back from a call.
        var result = RecycleBinResult.Create(["a"], [], [], TimeSpan.Zero);

// Succeeded means gone from disk, not necessarily recycled: a file removed permanently under an
        // explicit policy counts as a success. Check PermanentlyDeletedPaths when that matters. It is
        // also false when an item was left in a staging directory, so check LeftoverStagingDirectories
        // too rather than trusting one property to be the whole answer.
Console.WriteLine($"{result.Succeeded} {result.Elapsed} {result.FirstFailure is null}");
    }

    internal static async Task ListTheRecycleBin()
    {
        var contents = await RecycleBin.GetItemsAsync();

        foreach (var item in contents.Items)
        {
            // Size is 0 for a directory — its contents cannot be enumerated without elevation, so
            // IsDirectory is the honest signal that no figure is available.
            Console.WriteLine($"{item.DeletedAt:d}  {item.Size,12}  {item.OriginalPath}");
        }

        // TotalSize is derived from Items when the listing is built, so it cannot disagree with them.
        Console.WriteLine($"{contents.Items.Count} item(s), {contents.TotalSize} byte(s)");
    }

    internal static async Task ListOneVolumeAndHearAboutUnreadableEntries()
    {
        // The Recycle Bin is one Shell folder spanning every mounted volume, so this filters an
        // enumeration that has already happened rather than being a cheaper query.
        var onE = await RecycleBin.GetItemsAsync(@"X:\");

        foreach (var failure in onE.Failures)
        {
            Console.Error.WriteLine($"could not read an entry: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task EmptyAndReportWhatItFreed()
    {
        // Confirmation, progress window and sound are always suppressed: an unattended library must
        // never put a dialog on a caller's desktop.
        var emptied = await RecycleBin.EmptyAsync(@"X:\");

        // SHEmptyRecycleBin says nothing about how much it removed, so both figures are measured per
        // volume before and after. Neither is ever negative.
        Console.WriteLine($"freed {emptied.BytesFreed} byte(s) from {emptied.ItemsRemoved} item(s)");

        foreach (var failure in emptied.Failures)
        {
            Console.Error.WriteLine($"{failure.Path} was not emptied: 0x{failure.HResult:X8}");
        }
    }

    internal static async Task ActOnAVolumeWhoseRecycleBinIsOff()
    {
        // A volume root, not a directory inside one.
        var status = await RecycleBin.GetStatusAsync(@"X:\");

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
        IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        Console.WriteLine(result.Succeeded ? "recycled" : $"{result.Failures.Count} failed");
    }

    internal static async Task ReportEveryFailure()
    {
        // A path that is not there comes back as a failure and does not stop the other entries.
        IReadOnlyList<string?> paths = [@"C:\Projects\build-output", @"C:\Projects\gone.log"];
        var result = await FileRecycler.MoveToRecycleBinAsync(paths);

        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine(
                $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
        }
    }

    internal static async Task LongPathsIntoTheBin()
    {
        IReadOnlyList<string?> paths = [@"C:\Projects\a-very-long-path\build-output"];
        var options = new RecycleBinOptions
        {
            LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
        };

        // Past MAX_PATH the Shell deletes the file for good and reports a success code, so the path is
        // never handed over. This policy renames the item to a short path on its own volume — a rename,
        // so it costs nothing regardless of size — and recycles it from there.
        var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

        // The entry shows the staging path as the original location, not where the file lived. That is
        // inherent: the Shell records the path it was asked to delete.
        //
        // A directory left here still holds an item that could neither be recycled nor moved back. The
        // item is never deleted in that state, so it is recoverable — the caller has to move it out.
        // A batch spanning volumes can leave more than one, so read the list when that is possible.
        foreach (var directory in result.LeftoverStagingDirectories)
        {
            Console.Error.WriteLine($"still holding your data: {directory}");
        }
    }

    internal static async Task OptInToPermanentDeletion()
    {
        IReadOnlyList<string?> paths = [@"C:\Projects\a-very-long-path\archive.zip"];
        var options = new RecycleBinOptions
        {
            LongPathFailureAction = LongPathFailureAction.DeletePermanently
        };

        var result = await FileRecycler.MoveToRecycleBinAsync(paths, options);

        // Only files past MAX_PATH are affected; everything else still goes to the Recycle Bin.
        if (result.PermanentlyDeletedPaths.Count > 0)
        {
            Console.Error.WriteLine($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
        }
    }

    internal static async Task CancelTheWait()
    {
        IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Cancelling stops the wait, not the work: a round already handed to the Shell runs to
        // completion, so a cancelled call may still recycle its files.
        await FileRecycler.MoveToRecycleBinAsync(paths, cancellationToken: cts.Token);
    }

    internal static async Task RefuseABatchOnAVolumeWithoutARecycleBin()
    {
        IReadOnlyList<string?> paths = [@"F:\Reports\quarterly.xlsx"];
        var options = new RecycleBinOptions { ThrowOnError = false };

        // The default NoRecycleBinAction is Throw: on a volume with no Recycle Bin the Shell removes the
        // file for good and still reports success, so the path is refused before it ever reaches the
        // Shell. With ThrowOnError off the refusal comes back as data instead of an exception.
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
        IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
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

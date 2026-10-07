// Compile check for the README examples. Not registered as a test: it exists only to prove the
// documented API shapes actually compile as written.
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Docs;

internal static class ReadmeExamples
{
    internal static void QuickStart()
    {
        FileRecycler.MoveToRecycleBinAsync(@"C:\Users\me\Downloads\archive.zip").GetAwaiter().GetResult();
    }

    internal static void BatchReportingFailures()
    {
        var paths = Directory.GetFiles(@"C:\Users\me\Downloads", "*", SearchOption.TopDirectoryOnly);
        var result = FileRecycler.MoveToRecycleBinAsync(paths).GetAwaiter().GetResult();

        if (!result.Succeeded)
        {
            foreach (var failure in result.Failures)
            {
                Console.Error.WriteLine(
                    $"{failure.Path}: {failure.Stage} (0x{failure.HResult:X8}) after {failure.Attempts} attempt(s)");
            }
        }
    }

    internal static void ServiceNeverThrows()
    {
        IReadOnlyList<string?> paths = [];
        var options = new RecycleBinOptions { ThrowOnError = false };
        var result = FileRecycler.MoveToRecycleBinAsync(paths, options).GetAwaiter().GetResult();

        if (!result.Succeeded)
        {
            Console.WriteLine(result.Failures.Count);
        }
    }

    internal static void RecycleDirectory()
    {
        IReadOnlyList<string?> paths = [@"C:\Projects\build-output"];
        var result = FileRecycler.MoveToRecycleBinAsync(paths).GetAwaiter().GetResult();
        Console.WriteLine(result.Succeeded ? "recycled" : $"{result.Failures.Count} failed");
    }

    internal static void ReportEveryFailure()
    {
        var result = FileRecycler.MoveToRecycleBinAsync([]).GetAwaiter().GetResult();

        foreach (var failure in result.Failures)
        {
            Console.Error.WriteLine(
                $"{failure.Path} — {failure.Stage}, hresult=0x{failure.HResult:X8}, attempts={failure.Attempts}");
        }
    }

    internal static void LongPathsIntoTheBin()
    {
        var options = new RecycleBinOptions
        {
            LongPathFailureAction = LongPathFailureAction.ShortenPathAndRecycle
        };

        var result = FileRecycler.MoveToRecycleBinAsync([], options).GetAwaiter().GetResult();
        Console.WriteLine(result.LeftoverStagingDirectory);
    }

    internal static void OptInToPermanentDeletion()
    {
        var options = new RecycleBinOptions
        {
            LongPathFailureAction = LongPathFailureAction.DeletePermanently
        };

        var result = FileRecycler.MoveToRecycleBinAsync([], options).GetAwaiter().GetResult();

        if (result.PermanentlyDeletedPaths.Count > 0)
        {
            Console.Error.WriteLine($"{result.PermanentlyDeletedPaths.Count} file(s) were deleted, not recycled.");
        }
    }

    internal static void CancelTheWait()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        FileRecycler.MoveToRecycleBinAsync([], cancellationToken: cts.Token).GetAwaiter().GetResult();
    }

    internal static void AssertInATest()
    {
        var result = FileRecycler.MoveToRecycleBinAsync([]).GetAwaiter().GetResult();

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
}
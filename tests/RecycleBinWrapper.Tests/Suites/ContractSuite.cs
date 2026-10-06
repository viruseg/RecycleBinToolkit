using System.Runtime.InteropServices;
using RecycleBinWrapper.Tests.Host;
using Utils;

namespace RecycleBinWrapper.Tests.Suites;

internal static class ContractSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ContractSuite." + nameof(OptionDefaultsMatchSpec), OptionDefaultsMatchSpec),
        new TestCase("ContractSuite." + nameof(OptionsAreClampedByNormalize), OptionsAreClampedByNormalize),
        new TestCase("ContractSuite." + nameof(ResultSucceedsWhenNoFailures), ResultSucceedsWhenNoFailures),
        new TestCase("ContractSuite." + nameof(ResultFailsWhenAnyFailure), ResultFailsWhenAnyFailure),
        new TestCase("ContractSuite." + nameof(ThrowRecycleFailedCarriesHResult), ThrowRecycleFailedCarriesHResult),
        new TestCase("ContractSuite." + nameof(TargetKeepsInputAndFullPath), TargetKeepsInputAndFullPath),
    ];

    private static Task OptionDefaultsMatchSpec(TestContext context)
    {
        var options = RecycleBinOptions.Default;

        Check.Equal(6, options.MaxAttempts, "MaxAttempts default");
        Check.Equal(TimeSpan.FromMilliseconds(50), options.InitialRetryDelay, "InitialRetryDelay default");
        Check.Equal(TimeSpan.FromSeconds(30), options.OverallTimeout, "OverallTimeout default");
        Check.True(options.VerifyRecycleBinReceipt, "VerifyRecycleBinReceipt default is on");

        return Task.CompletedTask;
    }

    private static Task OptionsAreClampedByNormalize(TestContext context)
    {
        var clamped = new RecycleBinOptions
        {
            MaxAttempts = 0,
            InitialRetryDelay = TimeSpan.FromMilliseconds(-5),
            OverallTimeout = TimeSpan.FromSeconds(-1)
        }.Normalize();

        Check.Equal(1, clamped.MaxAttempts, "MaxAttempts is clamped to at least 1");
        Check.Equal(TimeSpan.Zero, clamped.InitialRetryDelay, "negative InitialRetryDelay becomes zero");
        Check.Equal(TimeSpan.Zero, clamped.OverallTimeout, "negative OverallTimeout becomes zero");

        var kept = new RecycleBinOptions
        {
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            OverallTimeout = TimeSpan.FromSeconds(5)
        }.Normalize();

        Check.Equal(3, kept.MaxAttempts, "a valid MaxAttempts survives normalisation");
        Check.Equal(TimeSpan.FromMilliseconds(10), kept.InitialRetryDelay, "a valid delay survives normalisation");
        Check.Equal(TimeSpan.FromSeconds(5), kept.OverallTimeout, "a valid timeout survives normalisation");

        return Task.CompletedTask;
    }

    private static Task ResultSucceedsWhenNoFailures(TestContext context)
    {
        var result = RecycleBinResult.Create(["a", "b"], [], TimeSpan.FromMilliseconds(12));

        Check.True(result.Succeeded, "no failures means success");
        Check.Equal(0, result.Failures.Count, "no failures recorded");
        Check.Equal(2, result.RecycledPaths.Count, "both recycled paths reported");
        Check.True(result.FirstFailure is null, "no first failure when the batch succeeded");
        Check.Equal(TimeSpan.FromMilliseconds(12), result.Elapsed, "elapsed time carried through");

        return Task.CompletedTask;
    }

    private static Task ResultFailsWhenAnyFailure(TestContext context)
    {
        var first = new RecycleFailure(@"E:\locked.bin", unchecked((int)0x80270027), 3, RecycleFailureStage.Performing);
        var second = new RecycleFailure(@"E:\gone.bin", unchecked((int)0x80070002), 1, RecycleFailureStage.PathValidation);

        var result = RecycleBinResult.Create(["a"], [first, second], TimeSpan.Zero);

        Check.False(result.Succeeded, "any failure means the batch did not succeed");
        Check.Equal(2, result.Failures.Count, "both failures recorded");
        Check.True(ReferenceEquals(first, result.FirstFailure), "FirstFailure is the first recorded failure");
        Check.Equal(1, result.RecycledPaths.Count, "successful paths are still reported alongside failures");

        return Task.CompletedTask;
    }

    private static Task ThrowRecycleFailedCarriesHResult(TestContext context)
    {
        const int hresult = unchecked((int)0x80270027);
        var failure = new RecycleFailure(@"E:\locked.bin", hresult, 3, RecycleFailureStage.Performing);

        var thrown = Check.Throws<IOException>(
            () => RecycleBinThrowHelper.ThrowRecycleFailed(failure),
            "a terminal recycle failure raises IOException");

        Check.Equal(hresult, thrown.HResult, "IOException carries the Shell HRESULT");
        Check.True(thrown.InnerException is COMException, "InnerException is a COMException");
        Check.Equal(hresult, thrown.InnerException?.HResult, "COMException carries the same HRESULT");

        return Task.CompletedTask;
    }

    private static Task TargetKeepsInputAndFullPath(TestContext context)
    {
        var target = new RecycleTarget(@"sub\..\a.bin", @"E:\a.bin");

        Check.Equal(@"sub\..\a.bin", target.InputPath, "the caller's path is preserved verbatim for diagnostics");
        Check.Equal(@"E:\a.bin", target.FullPath, "the normalised path is kept for Shell calls");

        return Task.CompletedTask;
    }
}
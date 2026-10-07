using System.Runtime.InteropServices;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

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
        new TestCase("ContractSuite." + nameof(TargetCarriesBothPathForms), TargetCarriesBothPathForms),
        new TestCase("ContractSuite." + nameof(ExtendedPrefixIsStrippedForTheShell), ExtendedPrefixIsStrippedForTheShell),
        new TestCase("ContractSuite." + nameof(ReceiptAcceptsAnExtendedLengthOriginal), ReceiptAcceptsAnExtendedLengthOriginal),
        new TestCase("ContractSuite." + nameof(ReceiptRejectsWhatItCannotProve), ReceiptRejectsWhatItCannotProve),
        new TestCase("ContractSuite." + nameof(JoiningFailuresKeepsTheRestOfTheOutcome), JoiningFailuresKeepsTheRestOfTheOutcome),
        new TestCase("ContractSuite." + nameof(ItemOutcomeNamesTheStageItFailedAt), ItemOutcomeNamesTheStageItFailedAt),
        new TestCase("ContractSuite." + nameof(ExtendedPrefixIsOnlyValidWhereItIs), ExtendedPrefixIsOnlyValidWhereItIs),
    ];

    /// <summary>
    /// The <c>\\?\</c> prefix is not a marker that can be put in front of any long path. It means
    /// "everything after this is a literal filesystem path", which is only true for a drive or volume
    /// path. Applied elsewhere it produces a path no API will open.
    /// </summary>
    private static Task ExtendedPrefixIsOnlyValidWhereItIs(TestContext context)
    {
        var shortDrive = @"E:\work\report.bin";
        Check.Equal(shortDrive, LongPathStager.PathForIo(shortDrive), "a path that fits is left exactly as it is");

        var longDrive = @"E:\" + new string('d', 300) + @"\report.bin";
        Check.Equal(@"\\?\" + longDrive, LongPathStager.PathForIo(longDrive), "a long drive path takes the plain prefix");

        var longUnc = @"\\server\share" + new string('d', 300) + @"\report.bin";
        Check.Equal(
            @"\\?\UNC\server\share" + new string('d', 300) + @"\report.bin",
            LongPathStager.PathForIo(longUnc),
            "a long UNC path needs \\?\\UNC\\, since \\?\\ immediately followed by another backslash is not a UNC path");

        var shortUnc = @"\\server\share\report.bin";
        Check.Equal(shortUnc, LongPathStager.PathForIo(shortUnc), "a UNC path that fits is left alone");

        var alreadyPrefixed = @"\\?\" + longDrive;
        Check.Equal(alreadyPrefixed, LongPathStager.PathForIo(alreadyPrefixed), "an already-prefixed path is not prefixed twice");

        var volume = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\" + new string('v', 300) + @"\report.bin";
        Check.Equal(volume, LongPathStager.PathForIo(volume), "an extended volume GUID path is already in extended form");

        var device = @"\\.\PhysicalDrive0" + new string('d', 300);
        Check.Equal(device, LongPathStager.PathForIo(device), "a device path is not a filesystem path and takes no prefix");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Each way one item's Shell result can fall short has to name itself, or a caller reading
    /// <see cref="RecycleFailure.Stage"/> is told the wrong thing about what went wrong.
    /// </summary>
    /// <remarks>
    /// The receipt rejection is the one that cannot be produced against the live Shell on demand, since
    /// it needs the Shell itself to name a destination outside the Recycle Bin. Pinning the decision here
    /// is what keeps that stage reachable at all: it used to be recorded after the general one, and
    /// because only the first verdict for a path is kept, the later write was dropped every time.
    /// </remarks>
    private static Task ItemOutcomeNamesTheStageItFailedAt(TestContext context)
    {
        const string original = @"E:\work\report.bin";
        const int Success = 0x00270008;
        const int Refused = unchecked((int)0x80070005);

        Check.Equal(
            null,
            RecycleOperationRunner.ClassifyOutcome(Success, false, @"E:\$Recycle.Bin\S-1-5\$RA.bin", true, original),
            "a success, a gone file and a Recycle Bin destination mean recycled");

        Check.Equal(
            RecycleFailureStage.Performing,
            RecycleOperationRunner.ClassifyOutcome(Refused, true, null, true, original),
            "the Shell refusing names Performing");

        Check.Equal(
            RecycleFailureStage.Performing,
            RecycleOperationRunner.ClassifyOutcome(Success, true, null, true, original),
            "a success for a file that is still there also names Performing");

        Check.Equal(
            RecycleFailureStage.ReceiptVerification,
            RecycleOperationRunner.ClassifyOutcome(Success, false, @"E:\Temp\$RA.bin", true, original),
            "a destination outside the Recycle Bin names ReceiptVerification, not Performing");

        Check.Equal(
            null,
            RecycleOperationRunner.ClassifyOutcome(Success, false, @"E:\Temp\$RA.bin", false, original),
            "the same destination is accepted when receipt verification is off");

        Check.Equal(
            null,
            RecycleOperationRunner.ClassifyOutcome(Success, false, null, true, original),
            "a delete that reported no destination stays unproven rather than failed");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A batch that rejects a path before the Shell is reached has two failure lists to join. Everything
    /// the operation already learned has to survive that join, in particular the two fields that report
    /// unrecoverable outcomes rather than refusals.
    /// </summary>
    private static Task JoiningFailuresKeepsTheRestOfTheOutcome(TestContext context)
    {
        var validated = new RecycleFailure(@"E:\absent.bin", 0, 0, RecycleFailureStage.PathValidation);
        var fromShell = new RecycleFailure(@"E:\locked.bin", unchecked((int)0x80070005), 2, RecycleFailureStage.Performing);

        var original = RecycleBinResult.Create(
            recycledPaths: [@"E:\recycled.bin"],
            permanentlyDeletedPaths: [@"E:\long.bin"],
            failures: [fromShell],
            elapsed: TimeSpan.FromMilliseconds(40),
            leftoverStagingDirectory: @"E:\$RCW-leftover");

        var joined = original.WithFailures([validated, fromShell]);

        Check.Equal(2, joined.Failures.Count, "both failures are carried");
        Check.True(ReferenceEquals(validated, joined.FirstFailure), "the joined failure comes first");

        Check.Equal(
            original.PermanentlyDeletedPaths.Count,
            joined.PermanentlyDeletedPaths.Count,
            "a permanent deletion reported by the operation survives the join");
        Check.Equal(
            @"E:\$RCW-leftover",
            joined.LeftoverStagingDirectory,
            "a staging directory left behind survives the join, which is the only trace the data is there");
        Check.Equal(1, joined.RecycledPaths.Count, "the recycled paths survive too");
        Check.Equal(original.Elapsed, joined.Elapsed, "and so does the elapsed time");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The receipt answers "did this land in the Recycle Bin on the item's own volume". Both inputs name
    /// the same place in different forms, and the form difference must not change the answer.
    /// </summary>
    private static Task ReceiptAcceptsAnExtendedLengthOriginal(TestContext context)
    {
        const string created = @"E:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RABCDEF.bin";

        Check.True(
            RecycleBinReceipt.IsInsideRecycleBin(created, @"E:\work\report.bin"),
            "a plain original and a plain Recycle Bin entry agree");
        Check.True(
            RecycleBinReceipt.IsInsideRecycleBin(created, @"\\?\E:\work\report.bin"),
            "the extended-length form of the same original names the same volume, so it agrees too");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Everything the receipt cannot call proof has to come back negative, because a positive answer is
    /// what tells the caller the bytes can be recovered.
    /// </summary>
    private static Task ReceiptRejectsWhatItCannotProve(TestContext context)
    {
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(null, @"E:\work\report.bin"),
            "no reported location proves nothing");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"", @"E:\work\report.bin"),
            "an empty reported location proves nothing");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"F:\$Recycle.Bin\S-1-5-21\$RX.bin", @"E:\work\report.bin"),
            "an entry on another volume is not this item's Recycle Bin");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"E:\Temp\$RABCDEF.bin", @"E:\work\report.bin"),
            "an entry outside the Recycle Bin folder was not recycled");
        Check.False(
            RecycleBinReceipt.IsInsideRecycleBin(@"E:\$Recycle.Bin\$RABCDEF.bin", @"relative\report.bin"),
            "an original that names no volume cannot be matched");

        return Task.CompletedTask;
    }

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
        var target = new RecycleTarget(@"sub\..\a.bin", @"E:\a.bin", @"E:\a.bin");

        Check.Equal(@"sub\..\a.bin", target.InputPath, "the caller's path is preserved verbatim for diagnostics");
        Check.Equal(@"E:\a.bin", target.FullPath, "the normalised path is kept for Shell calls");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The Shell's parser rejects the extended-length form with <c>E_INVALIDARG</c>, while the file
    /// APIs require it. Both forms therefore have to survive normalisation.
    /// </summary>
    private static Task TargetCarriesBothPathForms(TestContext context)
    {
        var extended = new RecycleTarget(@"\\?\E:\long.bin", @"\\?\E:\long.bin", @"E:\long.bin");

        Check.Equal(@"\\?\E:\long.bin", extended.FullPath, "the file-API form keeps the extended-length prefix");
        Check.Equal(@"E:\long.bin", extended.ShellPath, "the Shell form has no prefix");

        return Task.CompletedTask;
    }

    private static Task ExtendedPrefixIsStrippedForTheShell(TestContext context)
    {
        Check.Equal(@"E:\a.bin", RecycleTarget.StripExtendedPrefix(@"\\?\E:\a.bin"), "a drive path loses the prefix");
        Check.Equal(@"E:\a.bin", RecycleTarget.StripExtendedPrefix(@"E:\a.bin"), "a plain path is returned unchanged");
        Check.Equal(
            @"\\server\share\a.bin",
            RecycleTarget.StripExtendedPrefix(@"\\?\UNC\server\share\a.bin"),
            "an extended UNC path becomes the UNC form, which is already long-path safe");
        Check.Equal(
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.bin",
            RecycleTarget.StripExtendedPrefix(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.bin"),
            "an extended volume GUID path keeps its prefix, since no prefixless equivalent exists");

        return Task.CompletedTask;
    }
}
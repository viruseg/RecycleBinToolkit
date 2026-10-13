using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class ContractSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ContractSuite." + nameof(OptionDefaultsMatchSpec), OptionDefaultsMatchSpec),
        new TestCase("ContractSuite." + nameof(OptionsTypesExposeOnlyWhatTheirOperationReads), OptionsTypesExposeOnlyWhatTheirOperationReads),
        new TestCase("ContractSuite." + nameof(OptionsAreClampedByNormalize), OptionsAreClampedByNormalize),
        new TestCase("ContractSuite." + nameof(ResultSucceedsWhenNoFailures), ResultSucceedsWhenNoFailures),
        new TestCase("ContractSuite." + nameof(ResultFailsWhenAnyFailure), ResultFailsWhenAnyFailure),
        new TestCase("ContractSuite." + nameof(ThrowRecycleFailedCarriesHResult), ThrowRecycleFailedCarriesHResult),
        new TestCase("ContractSuite." + nameof(TargetKeepsInputAndFullPath), TargetKeepsInputAndFullPath),
        new TestCase("ContractSuite." + nameof(ExtendedPrefixIsStrippedForComparisons), ExtendedPrefixIsStrippedForComparisons),
        new TestCase("ContractSuite." + nameof(JoiningFailuresKeepsTheRestOfTheOutcome), JoiningFailuresKeepsTheRestOfTheOutcome),
        new TestCase("ContractSuite." + nameof(AnAbsurdRetryBudgetDoesNotOverflowTheDeadline), AnAbsurdRetryBudgetDoesNotOverflowTheDeadline),
        new TestCase("ContractSuite." + nameof(SurvivingStageNumbersAreFrozen), SurvivingStageNumbersAreFrozen),
        new TestCase("ContractSuite." + nameof(ProgressStatusNumbersAreFrozen), ProgressStatusNumbersAreFrozen),
        new TestCase("ContractSuite." + nameof(IndeterminateRefusesExactlyLikeNoRecycleBinOnVolume), IndeterminateRefusesExactlyLikeNoRecycleBinOnVolume),
        new TestCase("ContractSuite." + nameof(ATargetCostsNoAllocationOfItsOwn), ATargetCostsNoAllocationOfItsOwn),
        new TestCase("ContractSuite." + nameof(ContentsTotalIsDerivedNotDeclared), ContentsTotalIsDerivedNotDeclared),
        new TestCase("ContractSuite." + nameof(ContentsKeepsOneGroupPerVolumeWithItsRoot), ContentsKeepsOneGroupPerVolumeWithItsRoot),
        new TestCase("ContractSuite." + nameof(FailureHResultIsNotClobberedByTheSeverityBitTest), FailureHResultIsNotClobberedByTheSeverityBitTest),
        new TestCase("ContractSuite." + nameof(PathsThatResolveToOneItemRecycleOnce), PathsThatResolveToOneItemRecycleOnce),
        new TestCase("ContractSuite." + nameof(EveryReasonAndActionPairIsPinned), EveryReasonAndActionPairIsPinned),
        new TestCase("ContractSuite." + nameof(OnlyTheVolumeGuidDeviceFormCountsAsOne), OnlyTheVolumeGuidDeviceFormCountsAsOne),
        new TestCase("ContractSuite." + nameof(ThePayloadPathIsTheRecordsNameWithItsPrefixSwapped), ThePayloadPathIsTheRecordsNameWithItsPrefixSwapped),
    ];

    /// <summary>
    /// The one path shape nothing above this library can read, told apart from the paths that merely
    /// contain its text.
    /// </summary>
    /// <remarks>
    /// Anchored to the device prefix, because <c>Volume{</c> is also a legal directory name: a relative
    /// <c>Volume{guid}\file</c> resolves under the process's current directory to an ordinary path, and
    /// refusing that would be narrowing the accepted input without saying so. The forward-slash device
    /// spelling is checked against the normalised path rather than here — <c>Path.GetFullPath</c> rewrites
    /// it into the backslash form, which is what makes one call site need both checks.
    /// </remarks>
    private static Task OnlyTheVolumeGuidDeviceFormCountsAsOne(TestContext context)
    {
        Check.True(
            RecycleTarget.IsVolumeGuidPath(@"\\?\Volume{0be31b11-9126-4277-afd6-019ff4a1ee40}\file.bin"),
            "the device form is the only spelling such a volume has, so it is refused");

        Check.True(
            RecycleTarget.IsVolumeGuidPath(@"\\?\VOLUME{0BE31B11-9126-4277-AFD6-019FF4A1EE40}\file.bin"),
            "and the spelling of its own name does not decide it");

        Check.False(
            RecycleTarget.IsVolumeGuidPath(@"\\?\UNC\server\share\Volume{0be31b11}\file.bin"),
            "a share is not a volume, however its directories are named");

        Check.False(RecycleTarget.IsVolumeGuidPath(@"\\server\share\file.bin"), "nor is a UNC path");

        Check.False(RecycleTarget.IsVolumeGuidPath(@"W:\Volume{0be31b11-9126-4277-afd6-019ff4a1ee40}\file.bin"), "nor a directory on a volume that happens to be named that");

        Check.False(
            RecycleTarget.IsVolumeGuidPath(@"Volume{0be31b11-9126-4277-afd6-019ff4a1ee40}\file.bin"),
            "nor a relative path, which resolves under the current directory to an ordinary one");

        Check.False(RecycleTarget.IsVolumeGuidPath(@"W:\work\report.bin"), "and an ordinary path is unaffected");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The caller's three answers to "this volume cannot recycle" have to be pinned against every reason,
    /// because the pairs are what decides whether a file survives and there are few enough of them to
    /// state completely. The numbers behind both enums have to be pinned for the same reason: each
    /// declares that its ordinals are part of its public contract, so a renumbering is a breaking change
    /// that every compiler and every test in this file would accept.
    /// </summary>
    /// <remarks>
    /// A new reason added without a row here would be unasserted, so the table is checked against the
    /// enum as well. The two rows that share a rule — <c>Indeterminate</c> and
    /// <c>NoRecycleBinOnVolume</c> — are separated deliberately: an unconfigured volume is a doubt, not
    /// a proof, and <c>AllowIndeterminate</c> is the caller's way of saying they accept being wrong
    /// about it.
    /// </remarks>
    private static Task EveryReasonAndActionPairIsPinned(TestContext context)
    {
        (RecycleBinAvailabilityReason Reason, bool UnderThrow, bool UnderAllowIndeterminate, bool UnderDeletePermanently)[] table =
        [
            (RecycleBinAvailabilityReason.Enabled, false, false, false),
            (RecycleBinAvailabilityReason.DisabledByPolicy, true, true, false),
            (RecycleBinAvailabilityReason.DisabledByUserPreference, true, true, false),
            (RecycleBinAvailabilityReason.UnsupportedVolumeType, true, true, false),
            (RecycleBinAvailabilityReason.VolumeNotFound, true, true, true),
            (RecycleBinAvailabilityReason.Indeterminate, true, false, false),
            (RecycleBinAvailabilityReason.NoRecycleBinOnVolume, true, false, false)
        ];

        Check.Equal(
            Enum.GetValues<RecycleBinAvailabilityReason>().Length,
            table.Length,
            "every reason has a row, so a new one cannot join the enum unasserted");

        Check.Equal(3, Enum.GetValues<NoRecycleBinAction>().Length, "and every action is one of the three this table names");

        foreach (var (reason, underThrow, underAllowIndeterminate, underDeletePermanently) in table)
        {
            Check.Equal(
                underThrow,
                RecycleBinVolumePolicy.RefusesRecycle(reason, NoRecycleBinAction.Throw),
                $"{reason} under Throw");

            Check.Equal(
                underAllowIndeterminate,
                RecycleBinVolumePolicy.RefusesRecycle(reason, NoRecycleBinAction.AllowIndeterminate),
                $"{reason} under AllowIndeterminate");

            Check.Equal(
                underDeletePermanently,
                RecycleBinVolumePolicy.RefusesRecycle(reason, NoRecycleBinAction.DeletePermanently),
                $"{reason} under DeletePermanently");
        }

        Check.True(
            RecycleBinVolumePolicy.RefusesRecycle(RecycleBinAvailabilityReason.VolumeNotFound, NoRecycleBinAction.DeletePermanently),
            "a path that is not on disk is never handed over, so DeletePermanently does not reach it");

        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.Enabled, 0);
        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.DisabledByPolicy, 1);
        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.DisabledByUserPreference, 2);
        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.UnsupportedVolumeType, 3);
        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.VolumeNotFound, 4);
        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.NoRecycleBinOnVolume, 5);
        ReasonOrdinalIsPinned(RecycleBinAvailabilityReason.Indeterminate, 6);

        Check.Equal(0, (int)NoRecycleBinAction.Throw, "Throw keeps number 0");
        Check.Equal(1, (int)NoRecycleBinAction.AllowIndeterminate, "AllowIndeterminate keeps number 1");
        Check.Equal(2, (int)NoRecycleBinAction.DeletePermanently, "DeletePermanently keeps number 2");

        return Task.CompletedTask;
    }

    private static void ReasonOrdinalIsPinned(RecycleBinAvailabilityReason reason, int expected) =>
        Check.Equal(expected, (int)reason, $"{reason} keeps number {expected}, which its remarks call part of its contract");

    /// <summary>
    /// Two paths that name one file must recycle once, however differently they are written.
    /// </summary>
    /// <remarks>
    /// De-duplication compares the normalised path, so both spellings have to collapse before anything
    /// is moved. Moving two entries for one file is what the check prevents: the second would be asked
    /// to move something the first had already moved, and the entry that attempt creates would be
    /// reported as an item of its own.
    /// </remarks>
    private static Task PathsThatResolveToOneItemRecycleOnce(TestContext context)
    {
        const string plain = @"W:\work\report.bin";

        Check.Equal(
            1,
            RecycleTarget.CollapseEquivalent(Targets(plain, plain.ToUpperInvariant())).Count,
            "the case of the path does not make it a second item");

        Check.Equal(
            2,
            RecycleTarget.CollapseEquivalent(Targets(plain, @"W:\work\other.bin")).Count,
            "two different files are two items");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A failure's HRESULT is what an integrator looks up, so the value on it has to be the file
    /// system's code and nothing else.
    /// </summary>
    /// <remarks>
    /// The documented way to tell a failure from a success code is to test the severity bit. That only
    /// holds if the severity bit is never set by anything but the code itself: a failure recorded with
    /// a code of zero would otherwise read as a success, and one recorded with the bit already set would
    /// read as a failure even when the move reported success. Both cases are what this pins — the stage
    /// describes where it failed, the HRESULT says what the file system said.
    /// </remarks>
    private static Task FailureHResultIsNotClobberedByTheSeverityBitTest(TestContext context)
    {
        const int refused = unchecked((int)0x80070020);

        var genuine = new RecycleFailure(@"W:\b.bin", refused, 3, RecycleFailureStage.Performing);
        Check.True(
            (genuine.HResult & unchecked((int)0x80000000)) != 0,
            "a refusal reads as a failure, which is what the severity bit is for");

        var validation = new RecycleFailure(@"W:\d.bin", 0, 0, RecycleFailureStage.PathValidation);
        Check.Equal(0, validation.HResult, "a validation failure carries no code because nothing was attempted");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A constructed listing must already state its own total, and a listing built around items that were
    /// added afterwards must not claim one.
    /// </summary>
    /// <remarks>
    /// The total was a settable property alongside a public constructor, so a caller could build a
    /// listing and leave it claiming zero bytes for a Recycle Bin holding files. It is now derived from
    /// the items at construction, which is the only place the items and the total can disagree if the
    /// type is ever changed again.
    /// </remarks>
    private static Task ContentsTotalIsDerivedNotDeclared(TestContext context)
    {
        var items = new[]
        {
            new RecycleBinItem(@"W:\$Recycle.Bin\S\$IA.bin", @"W:\$Recycle.Bin\S\$RA.bin", 4096, false, @"W:\a.bin", default),
            new RecycleBinItem(@"W:\$Recycle.Bin\S\$IB.bin", @"W:\$Recycle.Bin\S\$RB.bin", 1024, false, @"W:\b.bin", default)
        };

        var contents = new RecycleBinContents(
            [new RecycleBinVolumeContents(@"W:\", items)],
            [],
            TimeSpan.FromMilliseconds(5));

        Check.Equal(5120L, contents.TotalSize, "the total is the sum of the items' sizes");
        Check.Equal(1, contents.Items.Count, "the volume is carried through");
        Check.Equal(2, contents.Items[0].Items.Count, "the items are carried through");
        Check.Equal(TimeSpan.FromMilliseconds(5), contents.Elapsed, "the elapsed time is carried through");

        var empty = new RecycleBinContents([], [], TimeSpan.Zero);
        Check.Equal(0L, empty.TotalSize, "a listing with no items totals nothing rather than claiming a default");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A listing answers one group per volume, each naming the volume it belongs to, and a volume whose bin
    /// is empty is still one of them.
    /// </summary>
    /// <remarks>
    /// Volumes are read in parallel, so the grouping is what keeps a caller able to tell which disk an
    /// entry came from. A volume read with nothing in it keeps its place with an empty group rather than
    /// dropping out: a caller that asked about <c>X:\</c> and found no group for it could not tell an empty
    /// bin from a volume that was never reached, which is the same reason a root naming no volume is answered
    /// as a failure rather than as an empty result.
    /// </remarks>
    private static Task ContentsKeepsOneGroupPerVolumeWithItsRoot(TestContext context)
    {
        var populated = new[]
        {
            new RecycleBinItem(@"W:\$Recycle.Bin\S\$IA.bin", @"W:\$Recycle.Bin\S\$RA.bin", 4096, false, @"W:\a.bin", default)
        };

        var contents = new RecycleBinContents(
            [
                new RecycleBinVolumeContents(@"W:\", populated),
                new RecycleBinVolumeContents(@"X:\", [])
            ],
            [],
            TimeSpan.Zero);

        Check.Equal(2, contents.Items.Count, "both volumes are answered, the empty one included");
        Check.Equal(@"W:\", contents.Items[0].VolumeRoot, "a group's volume root is the root it was read from");
        Check.Equal(1, contents.Items[0].Items.Count, "the populated volume keeps its entry");
        Check.Equal(0, contents.Items[1].Items.Count, "a volume read with nothing in it is an empty group rather than an absent one");
        Check.Equal(@"X:\", contents.Items[1].VolumeRoot, "the empty group still names its volume");
        Check.Equal(4096L, contents.TotalSize, "the total spans every volume's groups");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds targets the way the facade does.
    /// </summary>
    private static IReadOnlyList<RecycleTarget> Targets(params string[] fullPaths)
    {
        var targets = new List<RecycleTarget>(fullPaths.Length);

        foreach (var fullPath in fullPaths)
        {
            targets.Add(new RecycleTarget(fullPath, fullPath, FileAttributes.Normal, 0));
        }

        return targets;
    }

    /// <summary>
    /// A batch is a list of targets, and the list is held for the whole operation, so what a target costs
    /// is a per-path cost across every path the caller ever passes.
    /// </summary>
    /// <remarks>
    /// As a class, every path costs an object header and three references on top of the slot it occupies
    /// in the list. A hundred thousand paths is a few megabytes of short-lived garbage for a move,
    /// which is the wrong place to pay. Measured rather than
    /// asserted on the declaration, because that is the property that matters and it does not care how
    /// the type is spelled.
    /// <para>
    /// The bound is a few bytes per target against roughly forty for a reference type. It is not tight,
    /// and does not need to be: anything above zero per path means a target is still escaping.
    /// </para>
    /// </remarks>
    private static Task ATargetCostsNoAllocationOfItsOwn(TestContext context)
    {
        const int Count = 200_000;
        const int BytesPerTargetBudget = 8;

        RecycleTarget[] warm = new RecycleTarget[1];
        warm[0] = new RecycleTarget("a", "b", FileAttributes.Normal, 0);
        GC.KeepAlive(warm);

        // The array is allocated before the measurement starts, so what is counted is the cost of
        // constructing the targets rather than the storage the list needs either way.
        var targets = new RecycleTarget[Count];

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < Count; i++)
        {
            targets[i] = new RecycleTarget("input", "full", FileAttributes.Normal, 0);
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var perTarget = allocated / (double)Count;

        GC.KeepAlive(targets);

        Check.True(
            perTarget < BytesPerTargetBudget,
            $"a target allocates nothing of its own (got {perTarget:F2} bytes per target over {Count} of them, budget {BytesPerTargetBudget})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A volume root is documented as always carrying a trailing separator, and the volume
    /// APIs require one. The tricky input is a bare drive letter: <c>Path.GetPathRoot</c> answers
    /// <c>W:</c> for it, which is a drive-relative path rather than a root, and which
    /// <c>GetVolumeNameForVolumeMountPoint</c> rejects.
    /// </summary>
    /// <remarks>
    /// The extended-length root is a root by the same rule and not by luck: <c>Path.GetPathRoot</c> hands
    /// <c>\\?\W:\</c> back verbatim, so reducing both sides makes them agree, where a prefixed path inside
    /// the volume answers <c>\\?\W:\</c> for its root and so stays distinguishable from it.
    /// </remarks>
    private static Task VolumeRootIsAlwaysARootWithASeparator(TestContext context)
    {
        Check.Equal(@"W:\", RecycleBinVolumeState.NormalizeVolumeRoot(@"W:"), "a bare drive letter gains its separator");
        Check.Equal(@"W:\", RecycleBinVolumeState.NormalizeVolumeRoot(@"W:\"), "a root that already has one is unchanged");
        Check.Equal(@"W:\", RecycleBinVolumeState.NormalizeVolumeRoot(@"W:\work"), "a path inside a volume collapses to its root");
        Check.Equal(@"w:\", RecycleBinVolumeState.NormalizeVolumeRoot(@"w:"), "a lower-case drive letter keeps its case and gains its separator");

        Check.Equal(
            @"\\server\share" + Path.DirectorySeparatorChar,
            RecycleBinVolumeState.NormalizeVolumeRoot(@"\\server\share"),
            "a UNC share is a volume root too");
        Check.Equal(
            @"\\server\share" + Path.DirectorySeparatorChar,
            RecycleBinVolumeState.NormalizeVolumeRoot(@"\\server\share\work\report.bin"),
            "a path inside a UNC share collapses to the share root");

        Check.Equal(
            @"W:\",
            RecycleBinVolumeState.NormalizeVolumeRoot(@"\\?\W:\"),
            "an extended-length root loses its prefix and keeps its separator");
        Check.True(
            RecycleBinVolumeState.IsVolumeRoot(@"\\?\W:\"),
            "and is read as the root of a volume, since the path hands its own spelling back as the root");
        Check.False(
            RecycleBinVolumeState.IsVolumeRoot(@"\\?\W:\work\report.bin"),
            "while a prefixed path inside one is not, because its root is the shorter string");

        return Task.CompletedTask;
    }


    /// <summary>
    /// The numbers behind the surviving stage names are part of their contract, so a renumbering that
    /// still compiles is a breaking change. The gaps — 1, 2, 4, 5 — are stages the removed Shell path
    /// reported; they stay empty rather than reused.
    /// </summary>
    private static Task SurvivingStageNumbersAreFrozen(TestContext context)
    {
        Check.Equal(0, (int)RecycleFailureStage.PathValidation, "PathValidation keeps number 0");
        Check.Equal(3, (int)RecycleFailureStage.Performing, "Performing keeps number 3");
        Check.Equal(6, (int)RecycleFailureStage.Enumeration, "Enumeration keeps number 6");
        Check.Equal(7, (int)RecycleFailureStage.PermanentDeleteFailed, "PermanentDeleteFailed keeps number 7");
        Check.Equal(8, (int)RecycleFailureStage.NoRecycleBinOnVolume, "NoRecycleBinOnVolume keeps number 8, declared above the two that follow it in the source");
        Check.Equal(9, (int)RecycleFailureStage.VolumeGuidPathNotAddressable, "VolumeGuidPathNotAddressable takes number 9");
        Check.Equal(10, (int)RecycleFailureStage.RestoreFailed, "RestoreFailed takes number 10, past every stage that existed before a restore");
        Check.Equal(11, (int)RecycleFailureStage.RestoreConflictRefused, "RestoreConflictRefused takes number 11, after it");
        Check.Equal(12, (int)RecycleFailureStage.LongPathNotRecyclable, "LongPathNotRecyclable takes number 12, past every stage that existed before it");
        Check.Equal(
            9,
            Enum.GetValues<RecycleFailureStage>().Length,
            "nine stages survive, so a new stage that reuses a gap is caught here rather than in a caller's persisted value");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The numbers behind the three progress enums are part of their contract for the same reason the
    /// stage numbers are: a caller may persist one across versions of this library.
    /// </summary>
    private static Task ProgressStatusNumbersAreFrozen(TestContext context)
    {
        Check.Equal(0, (int)RecycleStatus.Recycled, "Recycled keeps number 0");
        Check.Equal(1, (int)RecycleStatus.Failed, "Failed keeps number 1");
        Check.Equal(2, (int)RecycleStatus.Refused, "Refused keeps number 2");
        Check.Equal(3, (int)RecycleStatus.DeletedPermanently, "DeletedPermanently keeps number 3");
        Check.Equal(4, Enum.GetValues<RecycleStatus>().Length, "and nothing else joins the recycle enum");

        Check.Equal(0, (int)RestoreStatus.Restored, "Restored keeps number 0");
        Check.Equal(1, (int)RestoreStatus.Failed, "Failed keeps number 1");
        Check.Equal(2, (int)RestoreStatus.Skipped, "Skipped keeps number 2");
        Check.Equal(3, (int)RestoreStatus.Merged, "Merged keeps number 3");
        Check.Equal(4, Enum.GetValues<RestoreStatus>().Length, "and nothing else joins the restore enum");

        Check.Equal(0, (int)EmptyStatus.Removed, "Removed keeps number 0");
        Check.Equal(1, (int)EmptyStatus.Failed, "Failed keeps number 1");
        Check.Equal(2, Enum.GetValues<EmptyStatus>().Length, "and nothing else joins the empty enum");

        return Task.CompletedTask;
    }

    /// <summary>
    /// An unconfigured volume answers <see cref="RecycleBinAvailabilityReason.Indeterminate"/> rather than
    /// <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/>, and the two refuse exactly alike.
    /// </summary>
    /// <remarks>
    /// The decision table in <see cref="EveryReasonAndActionPairIsPinned"/> already pins the pairing; this
    /// states the equivalence directly, because it is the property that lets a member go unproduced without
    /// changing what any caller is told to do.
    /// </remarks>
    private static Task IndeterminateRefusesExactlyLikeNoRecycleBinOnVolume(TestContext context)
    {
        foreach (var action in Enum.GetValues<NoRecycleBinAction>())
        {
            Check.Equal(
                RecycleBinVolumePolicy.RefusesRecycle(RecycleBinAvailabilityReason.NoRecycleBinOnVolume, action),
                RecycleBinVolumePolicy.RefusesRecycle(RecycleBinAvailabilityReason.Indeterminate, action),
                $"Indeterminate refuses exactly like NoRecycleBinOnVolume under {action}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A batch that rejects a path before the move has two failure lists to join. Everything
    /// the operation already learned has to survive that join, in particular the field that reports
    /// an unrecoverable outcome rather than a refusal.
    /// </summary>
    private static Task JoiningFailuresKeepsTheRestOfTheOutcome(TestContext context)
    {
        var validated = new RecycleFailure(@"W:\absent.bin", 0, 0, RecycleFailureStage.PathValidation);
        var refused = new RecycleFailure(@"W:\locked.bin", unchecked((int)0x80070005), 2, RecycleFailureStage.Performing);

        var original = RecycleBinResult.Create(
            recycledPaths: [@"W:\recycled.bin"],
            permanentlyDeletedPaths: [@"W:\long.bin"],
            failures: [refused],
            elapsed: TimeSpan.FromMilliseconds(40));

        var joined = original.WithFailures([validated, refused]);

        Check.Equal(2, joined.Failures.Count, "both failures are carried");
        Check.True(ReferenceEquals(validated, joined.FirstFailure), "the joined failure comes first");

        Check.Equal(
            original.PermanentlyDeletedPaths.Count,
            joined.PermanentlyDeletedPaths.Count,
            "a permanent deletion reported by the operation survives the join");
        Check.Equal(1, joined.RecycledPaths.Count, "the recycled paths survive too");
        Check.Equal(original.Elapsed, joined.Elapsed, "and so does the elapsed time");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every knob has to be survivable, because every knob is public. A budget near
    /// <see cref="TimeSpan.MaxValue"/> used to overflow when the runner added the time already spent to
    /// it, and a doubled delay large enough to outgrow the wait window used to throw out of
    /// <c>TimeSpan.FromMilliseconds</c>. Both are arithmetic, not failure: there is nothing for a caller
    /// to do differently, so neither may reach them.
    /// </summary>
    private static async Task AnAbsurdRetryBudgetDoesNotOverflowTheDeadline(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("a Recycle Bin for the file under the absurd budget to reach");

        var file = workspace.CreateRandomFile(4096);

        var absurd = new RecycleOptions
        {
            // The budget that overflows when added to a non-zero elapsed time, and an attempt count large
            // enough that the doubled delay would exceed what a wait can express if the operation ever
            // had to wait at all.
            MaxAttempts = 200,
            InitialRetryDelay = TimeSpan.FromDays(1),
            OverallTimeout = TimeSpan.MaxValue,
            ThrowOnError = false
        };

        var normalized = absurd.Normalize();

        Check.True(
            normalized.OverallTimeout < TimeSpan.MaxValue,
            $"the budget is pulled back far enough that adding the elapsed time cannot overflow (got {normalized.OverallTimeout})");

        var result = await RecycleBin
            .RecycleAsync([file], absurd, context.CancellationToken)
            .ConfigureAwait(false);

        Check.True(result.Succeeded, $"an absurd budget still recycles the file (first failure: {result.FirstFailure?.Stage} 0x{result.FirstFailure?.HResult:X8})");
        Check.False(File.Exists(file), "and really removed it");

        workspace.Dispose();
    }

    private static Task OptionDefaultsMatchSpec(TestContext context)
    {
        var options = RecycleOptions.Default;

        Check.Equal(6, options.MaxAttempts, "MaxAttempts default");
        Check.Equal(TimeSpan.FromMilliseconds(50), options.InitialRetryDelay, "InitialRetryDelay default");
        Check.Equal(TimeSpan.FromSeconds(30), options.OverallTimeout, "OverallTimeout default");

        // The same hazard as NoRecycleBinAction below, one level up: ThrowOnError decides whether a
        // documented refusal is an exception or data, so a flip would silently take it away from every
        // caller who never asked about it.
        Check.True(options.ThrowOnError, "ThrowOnError default is on, so a documented refusal still raises");

        // Flipping this one default would make every caller destroy files on a volume that cannot
        // recycle them, so it is pinned by name rather than left to the property initialiser.
        Check.Equal(
            NoRecycleBinAction.Throw,
            options.NoRecycleBinAction,
            "NoRecycleBinAction defaults to Throw, which refuses instead of destroying");

        // The other two types carry their own Default, so their defaults are pins of their own rather than
        // something the recycle type happens to also say.
        Check.False(GetItemsOptions.Default.PreloadItemDetails, "PreloadItemDetails default");
        Check.Equal(
            RestoreDirectoryConflict.MergeSkip,
            RestoreOptions.Default.DirectoryConflict,
            "DirectoryConflict default on the restore type");
        Check.Equal(RestoreFileConflict.Skip, RestoreOptions.Default.FileConflict, "FileConflict default on the restore type");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A knob an operation does not read is worse than no knob: a caller sets it, watches nothing happen and
    /// has a debugging session instead of an answer. The type each value is passed as is what says which
    /// knobs that operation honours, so the surface is pinned by reflection — which, unlike the friend
    /// assembly's compile-time access, reports an internal property as absent.
    /// </summary>
    /// <remarks>
    /// The second assertion compares the union of what the three types expose against the base's properties
    /// by name, so a ninth knob added to the base and left off every one of them is a failure rather than a
    /// pass with a longer list, and so is a rename that leaves the counts equal. It says nothing about a
    /// fourth type: these three are named here, and one added later is not measured until it is written here.
    /// </remarks>
    private static Task OptionsTypesExposeOnlyWhatTheirOperationReads(TestContext context)
    {
        var expected = new Dictionary<Type, string>
        {
            [typeof(RecycleOptions)] = "InitialRetryDelay,LongPathFailureAction,MaxAttempts,NoRecycleBinAction,OverallTimeout,ThrowOnError",
            [typeof(RestoreOptions)] = "DirectoryConflict,FileConflict,InitialRetryDelay,MaxAttempts,OverallTimeout",
            [typeof(GetItemsOptions)] = "PreloadItemDetails",
        };

        var exposed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (type, names) in expected)
        {
            var actual = string.Join(
                ",",
                type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => property.Name)
                    .OrderBy(name => name, StringComparer.Ordinal));

            Check.Equal(names, actual, $"{type.Name} exposes only what its operation reads");

            // From what reflection reported rather than from the literal the check above compared it
            // against: the union has to be the real surface of the type, not a second copy of the
            // expectation, or the next assertion restates the first one.
            foreach (var name in actual.Split(','))
            {
                exposed.Add(name);
            }
        }

        var stored = typeof(RecycleBinOptions)
            .GetProperties(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Check.Equal(
            string.Join(",", stored),
            string.Join(",", exposed.OrderBy(name => name, StringComparer.Ordinal)),
            "every property the engine reads is exposed by an options type, and nothing else is stored");

        return Task.CompletedTask;
    }

    private static Task OptionsAreClampedByNormalize(TestContext context)
    {
        var clamped = new RecycleOptions
        {
            MaxAttempts = 0,
            InitialRetryDelay = TimeSpan.FromMilliseconds(-5),
            OverallTimeout = TimeSpan.FromSeconds(-1)
        }.Normalize();

        Check.Equal(1, clamped.MaxAttempts, "MaxAttempts is clamped to at least 1");
        Check.Equal(TimeSpan.Zero, clamped.InitialRetryDelay, "negative InitialRetryDelay becomes zero");
        Check.Equal(TimeSpan.Zero, clamped.OverallTimeout, "negative OverallTimeout becomes zero");

        var kept = new RecycleOptions
        {
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            OverallTimeout = TimeSpan.FromSeconds(5),
            NoRecycleBinAction = NoRecycleBinAction.DeletePermanently,
        }.Normalize();

        Check.Equal(3, kept.MaxAttempts, "a valid MaxAttempts survives normalisation");
        Check.Equal(TimeSpan.FromMilliseconds(10), kept.InitialRetryDelay, "a valid delay survives normalisation");
        Check.Equal(TimeSpan.FromSeconds(5), kept.OverallTimeout, "a valid timeout survives normalisation");

        // Normalize copies the value, and a copy that quietly dropped it would hand the gate the
        // property initialiser's default for every caller that asked for anything else.
        Check.Equal(
            NoRecycleBinAction.DeletePermanently,
            kept.NoRecycleBinAction,
            "the chosen NoRecycleBinAction survives normalisation");

        return Task.CompletedTask;
    }

    private static Task ResultSucceedsWhenNoFailures(TestContext context)
    {
        var result = RecycleBinResult.Create(["a", "b"], [], [], TimeSpan.FromMilliseconds(12));

        Check.True(result.Succeeded, "no failures means success");
        Check.Equal(0, result.Failures.Count, "no failures recorded");
        Check.Equal(2, result.RecycledPaths.Count, "both recycled paths reported");
        Check.True(result.FirstFailure is null, "no first failure when the batch succeeded");
        Check.Equal(TimeSpan.FromMilliseconds(12), result.Elapsed, "elapsed time carried through");

        return Task.CompletedTask;
    }

    private static Task ResultFailsWhenAnyFailure(TestContext context)
    {
        var first = new RecycleFailure(@"W:\locked.bin", unchecked((int)0x80270027), 3, RecycleFailureStage.Performing);
        var second = new RecycleFailure(@"W:\gone.bin", unchecked((int)0x80070002), 1, RecycleFailureStage.PathValidation);

        var result = RecycleBinResult.Create(["a"], [], [first, second], TimeSpan.Zero);

        Check.False(result.Succeeded, "any failure means the batch did not succeed");
        Check.Equal(2, result.Failures.Count, "both failures recorded");
        Check.True(ReferenceEquals(first, result.FirstFailure), "FirstFailure is the first recorded failure");
        Check.Equal(1, result.RecycledPaths.Count, "successful paths are still reported alongside failures");

        return Task.CompletedTask;
    }

    private static Task ThrowRecycleFailedCarriesHResult(TestContext context)
    {
        const int hresult = unchecked((int)0x80270027);
        var failure = new RecycleFailure(@"W:\locked.bin", hresult, 3, RecycleFailureStage.Performing);

        var thrown = Check.Throws<IOException>(
            () => RecycleBinThrowHelper.ThrowRecycleFailed(failure),
            "a terminal recycle failure raises IOException");

        Check.Equal(hresult, thrown.HResult, "IOException carries the file-system HRESULT");
        Check.True(thrown.InnerException is COMException, "InnerException is a COMException");
        Check.Equal(hresult, thrown.InnerException?.HResult, "COMException carries the same HRESULT");

        return Task.CompletedTask;
    }

    private static Task TargetKeepsInputAndFullPath(TestContext context)
    {
        var target = new RecycleTarget(@"sub\..\a.bin", @"W:\a.bin", FileAttributes.Normal, 0);

        Check.Equal(@"sub\..\a.bin", target.InputPath, "the caller's path is preserved verbatim for diagnostics");
        Check.Equal(@"W:\a.bin", target.FullPath, "the normalised path is kept for the file APIs");

        return Task.CompletedTask;
    }

    private static Task ExtendedPrefixIsStrippedForComparisons(TestContext context)
    {
        Check.Equal(@"W:\a.bin", RecycleTarget.StripExtendedPrefix(@"\\?\W:\a.bin"), "a drive path loses the prefix");
        Check.Equal(@"W:\a.bin", RecycleTarget.StripExtendedPrefix(@"W:\a.bin"), "a plain path is returned unchanged");
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

    /// <summary>
    /// A payload is found by rewriting its record's second character, and this is every shape that rewrite
    /// has to answer for — including the ones it must decline.
    /// </summary>
    /// <remarks>
    /// Pinned as a table rather than left to the callers that happen to exercise it, because this is the one
    /// place that is written by hand rather than by the path API: it finds the name's
    /// start, decides whether the name is a record's at all, and joins the two halves back together. The
    /// rewrite was made cheaper by hand for the same reason, and a cheaper hand-written rewrite is exactly
    /// the kind of change that agrees with itself and disagrees with the filesystem.
    /// <para>
    /// The rows that matter most are the declines. A path that is not a record's comes back untouched, which
    /// is what lets a caller pass whatever it has, and a share spelled with forward slashes is answered in
    /// backslashes because the directory half comes from the path API rather than from a slice of the input.
    /// Both are answers a caller can notice, and neither is visible anywhere else in the suite.
    /// </para>
    /// </remarks>
    private static Task ThePayloadPathIsTheRecordsNameWithItsPrefixSwapped(TestContext context)
    {
        (string Record, string Payload)[] rewritten =
        [
            (@"W:\$RECYCLE.BIN\S-1-5-21-1001\$IABCDEF.bin", @"W:\$RECYCLE.BIN\S-1-5-21-1001\$RABCDEF.bin"),
            (// A share spelled with forward slashes comes back in backslashes, because the directory half is
             // the path API's spelling and not the caller's.
             "//server/share/$IABCDEF.bin", @"\\server\share\$RABCDEF.bin"),
            (// No directory at all, so no separator is inserted.
             "$IABCDEF.bin", "$RABCDEF.bin"),
            // A drive-relative record, where the root is "W:" and the name starts right after it.
            ("W:$IABC", @"W:\$RABC"),
            // The extended-length prefix is part of the path and is carried through.
            (@"\\?\W:\$IABCDEF.bin", @"\\?\W:\$RABCDEF.bin"),
            // The lowercase spelling is the one a case-sensitive volume could have written.
            (@"W:\$iLOWER.bin", @"W:\$RLOWER.bin"),
            // The extension is the caller's file's and is carried through untouched, including its case.
            (@"C:\dir\$IX.PDF", @"C:\dir\$RX.PDF"),
            // The shortest name there is that still qualifies: the marker and the prefix, and nothing after.
            (@"W:\$I", @"W:\$R"),
            ("$I", "$R"),
        ];

        foreach (var (record, payload) in rewritten)
        {
            Check.Equal(payload, NativeBinLayout.ToPayloadPath(record), $"the payload beside {record}");
        }

        // Everything below is a path that is not a record's, and every one of them comes back exactly as it
        // went in — a caller holding something unexpected gets it back rather than a mangled version of it.
        string[] declined =
        [
            @"W:\$RECYCLE.BIN\S-1-5-21-1001\$RABCDEF.bin",
            @"W:\a.bin",
            @"W:\.hidden",
            @"W:\$IABCDEF.bin\",
            @"W:\$IABCDEF\",
            @"W:\",
            "",
        ];

        foreach (var path in declined)
        {
            Check.Equal(
                path,
                NativeBinLayout.ToPayloadPath(path),
                $"'{path}' is not a record, so it is handed back rather than rewritten");
        }

        return Task.CompletedTask;
    }
}
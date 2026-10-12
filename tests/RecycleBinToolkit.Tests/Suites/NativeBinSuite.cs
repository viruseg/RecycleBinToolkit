using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// The Recycle Bin's own files, written and read directly: index records and payloads under
/// <c>$RECYCLE.BIN</c>.
/// </summary>
/// <remarks>
/// Nothing here creates a file outside <see cref="ScratchWorkspace.Root"/>. Where a case needs a
/// Recycle Bin record shaped in a way the machine does not produce, the record is built
/// inside the scratch root and the internal reader is pointed at it; the record is only ever written
/// into <c>$RECYCLE.BIN</c> by the code under test.
/// </remarks>
internal static class NativeBinSuite
{
    /// <summary>Enough to be past the old 260-character limit.</summary>
    private const int PastMaxPath = 300;

/// <summary>
    /// Far enough that a version 2 record cannot be decoded from a fixed read buffer, which is where the
    /// rented read begins. Comfortably past rather than barely: the buffer is 1024 bytes and a record of
    /// this size needs about 1500, so a fixture that sat on the boundary would stop exercising the branch
    /// the moment the buffer grew by a character.
    /// </summary>
    private const int FarPastMaxPath = 700;

    /// <summary>Content every generated file carries, so a restore can be compared byte for byte.</summary>
    private static readonly byte[] Content = CreateContent();

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("NativeBinSuite." + nameof(NativeRecycleRemovesTheFileAndRecordsIt), NativeRecycleRemovesTheFileAndRecordsIt),
        new TestCase("NativeBinSuite." + nameof(ALongPathLeavesNoHelperDirectoriesBehind), ALongPathLeavesNoHelperDirectoriesBehind),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleAcceptsAPathPastMaxPath), NativeRecycleAcceptsAPathPastMaxPath),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleAcceptsAPathFarPastMaxPath), NativeRecycleAcceptsAPathFarPastMaxPath),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleMovesADirectory), NativeRecycleMovesADirectory),
        new TestCase("NativeBinSuite." + nameof(RecycledNamesClimbAboveWhateverTheBinAlreadyHolds), RecycledNamesClimbAboveWhateverTheBinAlreadyHolds),
        new TestCase("NativeBinSuite." + nameof(NativeIndexRecordCarriesVersionTwoAndTheOriginalPath), NativeIndexRecordCarriesVersionTwoAndTheOriginalPath),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleReportsALockedFileAndLeavesNoIndexRecord), NativeRecycleReportsALockedFileAndLeavesNoIndexRecord),
        new TestCase("NativeBinSuite." + nameof(NativeWriteSupportIsAvailableOnThisMachine), NativeWriteSupportIsAvailableOnThisMachine),
        new TestCase("NativeBinSuite." + nameof(NativeIndexReaderReadsAVersionOneRecord), NativeIndexReaderReadsAVersionOneRecord),
        new TestCase("NativeBinSuite." + nameof(NativeIndexReaderReadsARecordLongerThanTheStackBuffer), NativeIndexReaderReadsARecordLongerThanTheStackBuffer),
        new TestCase("NativeBinSuite." + nameof(NativeIndexReaderRejectsATruncatedRecord), NativeIndexReaderRejectsATruncatedRecord),
        new TestCase("NativeBinSuite." + nameof(NativeIndexReaderRejectsARecordOfUnknownVersion), NativeIndexReaderRejectsARecordOfUnknownVersion),
        new TestCase("NativeBinSuite." + nameof(NativeIndexReaderDropsARecordDeclaringAnImpossiblePathLength), NativeIndexReaderDropsARecordDeclaringAnImpossiblePathLength),
        new TestCase("NativeBinSuite." + nameof(NativeIndexReaderDropsARecordWhoseTimestampIsOutOfRange), NativeIndexReaderDropsARecordWhoseTimestampIsOutOfRange),
        new TestCase("NativeBinSuite." + nameof(ATakenEntryNameIsRefusedAsACollisionWhateverItsAttributes), ATakenEntryNameIsRefusedAsACollisionWhateverItsAttributes),
        new TestCase("NativeBinSuite." + nameof(ATakenNameIsRecognisedByItsCodeAndALockedFileIsNot), ATakenNameIsRecognisedByItsCodeAndALockedFileIsNot),
        new TestCase("NativeBinSuite." + nameof(NativeListingFindsAnEntryNativeRecycleRecorded), NativeListingFindsAnEntryNativeRecycleRecorded),
        new TestCase("NativeBinSuite." + nameof(NativeListingReportsARecycledDirectoryAsADirectory), NativeListingReportsARecycledDirectoryAsADirectory),
        new TestCase("NativeBinSuite." + nameof(NativeListingOfAVolumeWithNoBinDirectoryIsEmpty), NativeListingOfAVolumeWithNoBinDirectoryIsEmpty),
        new TestCase("NativeBinSuite." + nameof(NativeListingGroupsEveryVolumeAndKeepsTheirOrder), NativeListingGroupsEveryVolumeAndKeepsTheirOrder),
        new TestCase("NativeBinSuite." + nameof(NativeListingHidesAnIndexWhosePayloadIsGone), NativeListingHidesAnIndexWhosePayloadIsGone),
        new TestCase("NativeBinSuite." + nameof(NativeListingIsUndisturbedByARecordItCannotRead), NativeListingIsUndisturbedByARecordItCannotRead),
        new TestCase("NativeBinSuite." + nameof(ADriveIsClassifiedFromItsOwnSeekPenaltyAnswer), ADriveIsClassifiedFromItsOwnSeekPenaltyAnswer),
        new TestCase("NativeBinSuite." + nameof(VolumesAreGroupedByTheDiskTheyArePartitionsOf), VolumesAreGroupedByTheDiskTheyArePartitionsOf),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyRemovesWhatWasRecycledAndReportsExactCounts), NativeEmptyRemovesWhatWasRecycledAndReportsExactCounts),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyOfAVolumeWithNothingInItReportsNothing), NativeEmptyOfAVolumeWithNothingInItReportsNothing),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyOfNoVolumesIsAnEmptyResult), NativeEmptyOfNoVolumesIsAnEmptyResult),
        new TestCase("NativeBinSuite." + nameof(AnEmptyAcrossTwoDisksAnswersInTheOrderTheyWereAskedFor), AnEmptyAcrossTwoDisksAnswersInTheOrderTheyWereAskedFor),
        new TestCase("NativeBinSuite." + nameof(AnEmptyOfTheSameVolumeTwiceDoesNotCountItTwice), AnEmptyOfTheSameVolumeTwiceDoesNotCountItTwice),
        new TestCase("NativeBinSuite." + nameof(AnEmptyCancelledBeforeItStartsRaisesAndDoesNotAggregate), AnEmptyCancelledBeforeItStartsRaisesAndDoesNotAggregate),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyKeepsTheBinDirectoriesThemselves), NativeEmptyKeepsTheBinDirectoriesThemselves),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyHonoursCancellationBetweenEntries), NativeEmptyHonoursCancellationBetweenEntries),
        new TestCase("NativeBinSuite." + nameof(ARecycledItemRestoresFromItsRecordedPath), ARecycledItemRestoresFromItsRecordedPath),
        new TestCase("NativeBinSuite." + nameof(AnUnclampedAttemptBudgetStillRecycles), AnUnclampedAttemptBudgetStillRecycles),
        new TestCase("NativeBinSuite." + nameof(AnAbsurdOverallTimeoutDoesNotOverflow), AnAbsurdOverallTimeoutDoesNotOverflow),
        new TestCase("NativeBinSuite." + nameof(NativeOverallTimeoutBoundsTheWholeRetryBudget), NativeOverallTimeoutBoundsTheWholeRetryBudget),
        new TestCase("NativeBinSuite." + nameof(ARecycledEntryNeverHasItsRecordWithoutItsPayload), ARecycledEntryNeverHasItsRecordWithoutItsPayload),
        new TestCase("NativeBinSuite." + nameof(ADeepDirectoryMovesWhole), ADeepDirectoryMovesWhole),
        new TestCase("NativeBinSuite." + nameof(AnEmptyNamesOnlyTheVolumesItActuallyEmptied), AnEmptyNamesOnlyTheVolumesItActuallyEmptied),
        new TestCase("NativeBinSuite." + nameof(ABinWithNothingInItIsStillNamedAsEmptied), ABinWithNothingInItIsStillNamedAsEmptied),
        new TestCase("NativeBinSuite." + nameof(NativeEnumerationPairsIndexAndPayload), NativeEnumerationPairsIndexAndPayload),
        new TestCase("NativeBinSuite." + nameof(NativeListingDoesNotReadIndexRecords), NativeListingDoesNotReadIndexRecords),
        new TestCase("NativeBinSuite." + nameof(NativePreloadReportsAnIndexItCannotRead), NativePreloadReportsAnIndexItCannotRead),
        new TestCase("NativeBinSuite." + nameof(NativePreloadMakesTheDetailsReadAlready), NativePreloadMakesTheDetailsReadAlready),
        new TestCase("NativeBinSuite." + nameof(NativeLazyDetailsAreBothAnsweredByTheFirstAccess), NativeLazyDetailsAreBothAnsweredByTheFirstAccess),
        new TestCase("NativeBinSuite." + nameof(NativeLazyDetailsThrowOnceWhenIndexIsGone), NativeLazyDetailsThrowOnceWhenIndexIsGone),
        new TestCase("NativeBinSuite." + nameof(NativeListingIgnoresAPayloadWithoutARecord), NativeListingIgnoresAPayloadWithoutARecord),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyCollectsAnEntryWhosePayloadIsGone), NativeEmptyCollectsAnEntryWhosePayloadIsGone),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyIgnoresADirectoryNamedLikeARecord), NativeEmptyIgnoresADirectoryNamedLikeARecord),
        new TestCase("NativeBinSuite." + nameof(NativeLazyDetailsSayWhatHappenedWhenTheRecordIsNotARecord), NativeLazyDetailsSayWhatHappenedWhenTheRecordIsNotARecord),
        new TestCase("NativeBinSuite." + nameof(NativeLazyDetailsCarryTheFileSystemCode), NativeLazyDetailsCarryTheFileSystemCode),
        new TestCase("NativeBinSuite." + nameof(NativeEnumerationPairsHalvesWhateverCaseTheyAreWrittenIn), NativeEnumerationPairsHalvesWhateverCaseTheyAreWrittenIn),
        new TestCase("NativeBinSuite." + nameof(NativeListingReportsAZeroSizeForARecycledDirectory), NativeListingReportsAZeroSizeForARecycledDirectory),
        new TestCase("NativeBinSuite." + nameof(LoadItemDetailsReadsEveryPendingRecord), LoadItemDetailsReadsEveryPendingRecord),
        new TestCase("NativeBinSuite." + nameof(LoadItemDetailsSkipsWhatTheListingAlreadyRead), LoadItemDetailsSkipsWhatTheListingAlreadyRead),
        new TestCase("NativeBinSuite." + nameof(LoadItemDetailsLeavesAnUnreadableRecordCold), LoadItemDetailsLeavesAnUnreadableRecordCold),
        new TestCase("NativeBinSuite." + nameof(VolumeRootIsTheRecordsOwn), VolumeRootIsTheRecordsOwn),
        new TestCase("NativeBinSuite." + nameof(LoadDetailsStaysConsistentWhenManyThreadsRaceIt), LoadDetailsStaysConsistentWhenManyThreadsRaceIt),
    ];

    /// <summary>
    /// The path is gone from disk and the call reports it as recycled rather than as failed. That is the
    /// whole of the ordinary case: everything else here is about what the record says.
    /// </summary>
    private static async Task NativeRecycleRemovesTheFileAndRecordsIt(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the file to be recorded in");

        var path = CreateFile("ordinary.bin");
        var size = new FileInfo(path).Length;

        var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the file is recycled (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(1, result.RecycledPaths.Count, "and is named as recycled");
        Check.Equal(0, result.Failures.Count, "with no failure recorded");
        Check.False(File.Exists(path), $"and is gone from disk: {path}");

        var record = ReadNewestRecord(size, path);
        Check.Equal(size, record.Size, "the record carries the size the file had");

        workspace.Dispose();
    }

    /// <summary>
    /// A long path moves into the bin directly: the destination is about seventy-five characters whatever
    /// the source was, so no helper directories appear in the volume root.
    /// </summary>
    private static async Task ALongPathLeavesNoHelperDirectoriesBehind(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the long path to be recorded in");

        var before = CountHelperDirectories();
        var path = CreateDeepFile(PastMaxPath);

        var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"a long path recycles (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(before, CountHelperDirectories(), "and no helper directory appeared in the volume root while it ran");

        workspace.Dispose();
    }

    /// <summary>
    /// A path past <c>MAX_PATH</c> has no limit here: the record is written
    /// with the whole path in it and the payload moves to a short destination.
    /// </summary>
    private static async Task NativeRecycleAcceptsAPathPastMaxPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the long path to be recorded in");

        var path = CreateDeepFile(PastMaxPath);
        Check.True(path.Length >= PastMaxPath, $"the path is genuinely long ({path.Length} characters): {path}");

        var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"a path of {path.Length} characters recycles (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(0, result.PermanentlyDeletedPaths.Count, "and is not deleted permanently on the way");
        Check.False(File.Exists(path), "and is gone from disk");

        var record = ReadNewestRecord(Content.Length, path);
        Check.Equal(path, record.OriginalPath, "and the record names the whole original path, not a truncated one");

        workspace.Dispose();
    }

    /// <summary>
    /// The same at a length that cannot be decoded from a fixed read buffer, which is where a version 2
    /// record stops being a stack read and starts being a read into rented memory. A 480-character path is
    /// where the 1024-byte buffer runs out.
    /// </summary>
    private static async Task NativeRecycleAcceptsAPathFarPastMaxPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the long path to be recorded in");

        var path = CreateDeepFile(FarPastMaxPath);
        Check.True(path.Length >= FarPastMaxPath, $"the path is far past 260 characters ({path.Length} characters)");

        var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"a path of {path.Length} characters recycles (first failure: {Describe(result.FirstFailure)})");
        Check.False(File.Exists(path), "and is gone from disk");

        var record = ReadNewestRecord(Content.Length, path);
        Check.Equal(path, record.OriginalPath, "and the record names every character of it");

        workspace.Dispose();
    }

    /// <summary>
    /// A directory is one item, moved as a directory. Its record carries a size of zero, which is what
    /// Windows itself writes: a recycled directory's contents cannot be walked without elevation, so
    /// a number there would be a guess.
    /// </summary>
    private static async Task NativeRecycleMovesADirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the directory to be recorded in");

        var directory = Path.Combine(ScratchWorkspace.Root, "a-directory-" + RunTag);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "inside.bin"), Content);

        var result = await RecycleBin.RecycleAsync([directory], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the directory is recycled (first failure: {Describe(result.FirstFailure)})");
        Check.False(Directory.Exists(directory), "and is gone from disk");

        var record = ReadNewestRecord(0, directory);
        Check.Equal(0, record.Size, "and the record carries a size of zero, as Windows writes for a directory");
        Check.Equal(directory, record.OriginalPath, "with the directory's own path");

        workspace.Dispose();
    }

    /// <summary>
    /// Two files recycled in one call are given two names that climb, and a bin that already holds entries
    /// is never written into at or below the ones it holds.
    /// </summary>
    /// <remarks>
    /// <b>What this pins is an order, and the order is the whole of what changed.</b> The names are still six
    /// characters over the alphabet Windows uses, and the record is still created with
    /// <see cref="FileMode.CreateNew"/>, so nothing about safety rests on where they land — this is about
    /// where a batch of writes to one directory goes, not about whether they are allowed to.
    /// <list type="bullet">
    /// <item><b>One call, because two would prove something weaker.</b> A second call reads the bin again and
    /// starts above what the first left there. What that does not prove is that two names inside one call are
    /// adjacent, and the adjacency is the point.</item>
    /// <item><b>The assertion is the difference being one, not which is larger.</b> Which of the two came
    /// first is a property of nothing anyone depends on, and pinning it would make this a test of call
    /// order.</item>
    /// </list>
    /// </remarks>
    private static async Task RecycledNamesClimbAboveWhateverTheBinAlreadyHolds(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the names to be written into");

        var first = CreateFile("climbing-first");
        var second = CreateFile("climbing-second");

        var result = await RecycleBin.RecycleAsync([first, second], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(result.Succeeded, $"both files recycle in one call (first failure: {Describe(result.FirstFailure)})");

        var firstStem = StemOfRecordFor(first);
        var secondStem = StemOfRecordFor(second);

        Check.Equal(1, Math.Abs(firstStem - secondStem),
            $"the two names of one call are adjacent, so a batch of writes stays in one leaf of the bin's directory index (got {firstStem} and {secondStem})");

        workspace.Dispose();
    }

    /// <summary>
    /// The stem of the record written for <paramref name="originalPath"/>, as the number
    /// <see cref="NativeBinLayout.FormatStem"/> spells it.
    /// </summary>
    private static long StemOfRecordFor(string originalPath)
    {
        var indexPath = RecordOf(originalPath)
            ?? throw new InvalidOperationException($"no record in the bin names {originalPath}");

        var name = Path.GetFileName(indexPath);

        if (name.Length < NativeBinLayout.StemLength + 2
            || !NativeBinLayout.TryParseStem(name.AsSpan(2, NativeBinLayout.StemLength), out var index))
        {
            throw new InvalidOperationException($"the record is not named over this library's alphabet: {indexPath}");
        }

        return index;
    }
    /// <summary>
    /// The record this backend writes is the one Windows reads, so it has to say what Windows expects:
    /// version 2, the size, and the moment of deletion. A record that is merely readable by this library
    /// would not be: the Explorer's own Restore is the thing that has to work.
    /// </summary>
    private static async Task NativeIndexRecordCarriesVersionTwoAndTheOriginalPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the record to land in");

        var path = CreateFile("recorded.bin");
        var size = new FileInfo(path).Length;
        var before = DateTime.UtcNow.AddMinutes(-1);

        var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(result.Succeeded, $"the file is recycled (first failure: {Describe(result.FirstFailure)})");

        var record = ReadNewestRecord(size, path);

        Check.Equal(NativeIndexFile.Version, NativeIndexFile.ReadVersion(RecordOf(path)), "the record says version 2, which is what a Windows 10 1607 Shell reads");
        Check.Equal(size, record.Size, "the size is the file's own");
        Check.True(record.DeletedAtUtc >= before && record.DeletedAtUtc <= DateTime.UtcNow.AddMinutes(1),
            $"the deletion time is the moment of the call, not a file timestamp ({record.DeletedAtUtc:O})");
        Check.Equal(path, record.OriginalPath, "and the original path is the one the caller passed");

        workspace.Dispose();
    }

    /// <summary>
    /// A file held open without a delete share cannot be moved, and the record written for it must go with
    /// it. An index left behind names an entry whose payload does not exist, which Explorer then shows as
    /// a phantom a user can try to restore and cannot.
    /// </summary>
    private static async Task NativeRecycleReportsALockedFileAndLeavesNoIndexRecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the attempt to be measured against");

        var path = CreateFile("locked.bin");
        var indexesBefore = IndexRecords().Count;

        // FileShare.Read with no delete share is the deterministic way to make a move fail: the file stays
        // readable, so nothing else about it is wrong.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

            Check.False(result.Succeeded, "a file that cannot be moved is not reported as recycled");
            Check.Equal(1, result.Failures.Count, "and is named as a failure");
            Check.Equal(RecycleFailureStage.Performing, result.Failures[0].Stage, "at the stage where the move failed");
        }

        Check.Equal(indexesBefore, IndexRecords().Count, "the record written before the move was taken away again");
        Check.True(File.Exists(path), "and the caller's file is still where they left it");

        workspace.Dispose();
    }

    /// <summary>
    /// A bin can still hold records written by Windows 7 or 8, and a caller listing their own bin must not
    /// silently lose them. The format differs only in how the path is stored: a fixed 520-byte field rather
    /// than a length and the text.
    /// </summary>
    /// <remarks>
    /// The record is built inside the scratch root rather than written into the bin, because a test may not
    /// create a file outside the working directory <c>--scratch</c> names — and <c>$RECYCLE.BIN</c> is
    /// outside it. What is under test is the reader, and the reader takes a path.
    /// </remarks>
    private static Task NativeIndexReaderReadsAVersionOneRecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var original = Path.Combine(ScratchWorkspace.Root, "legacy-path.bin");
        var record = BuildRecord(original, size: 4096, deleted: new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc), version: 1);
        var path = Path.Combine(ScratchWorkspace.Root, "$Ilegacy.bin");
        File.WriteAllBytes(path, record);

        Check.True(NativeIndexFile.TryRead(path, out var read), "a version 1 record is readable");
        Check.Equal(original, read.OriginalPath, "with the path from its fixed field");
        Check.Equal(4096, read.Size, "and the size");
        Check.Equal(new DateTime(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc), read.DeletedAtUtc, "and the deletion time");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A path past about 480 characters does not fit the fixed read buffer, which is where a version 2
    /// record stops being a stack read and starts being a read into rented memory. A reader that only ever
    /// decoded what it had would truncate the path here and name a file that does not exist.
    /// </summary>
    private static Task NativeIndexReaderReadsARecordLongerThanTheStackBuffer(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var original = ScratchWorkspace.Root + new string('p', 600) + ".bin";
        var path = Path.Combine(ScratchWorkspace.Root, "$Ilong.bin");
        File.WriteAllBytes(path, BuildRecord(original, size: 17, deleted: DateTime.UtcNow, version: NativeIndexFile.Version));

        Check.True(original.Length * 2 > NativeIndexFile.StackBufferLength, $"the path is longer than the buffer holds ({original.Length * 2} bytes against {NativeIndexFile.StackBufferLength})");

        Check.True(NativeIndexFile.TryRead(path, out var read), "the record is readable");
        Check.Equal(original.Length, read.OriginalPath.Length, "and the path is not truncated");
        Check.Equal(original, read.OriginalPath, "every character of it survives");
        Check.Equal(17, read.Size, "with the size");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A record that is not a record — a file the enumeration picked up because its name begins with
    /// <c>$I</c> — must be refused rather than read as one. Reading past what was read would throw from
    /// inside a listing and cost the caller every other entry.
    /// </summary>
    private static Task NativeIndexReaderRejectsATruncatedRecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var path = Path.Combine(ScratchWorkspace.Root, "$Ishort.bin");
        File.WriteAllBytes(path, [1, 2, 3]);

        Check.False(NativeIndexFile.TryRead(path, out _), "a three-byte file is not a record");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A version this reader does not know is refused rather than guessed at, because guessing means reading
    /// the bytes after the header as a path when they are something else entirely.
    /// </summary>
    private static Task NativeIndexReaderRejectsARecordOfUnknownVersion(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var path = Path.Combine(ScratchWorkspace.Root, "$Ifuture.bin");
        File.WriteAllBytes(path, BuildRecord(@"W:\something.bin", size: 1, deleted: DateTime.UtcNow, version: 7));

        Check.False(NativeIndexFile.TryRead(path, out _), "a version 7 record is not read as a version 2 one");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The character count in a version 2 record is a number in a file, and the file sits in a directory any
    /// program running as this user can write to. Nothing makes it trustworthy, and a reader that takes it
    /// at its word rents a buffer of whatever size the header names — two gigabytes at the largest count its
    /// own overflow guard admits, which is an allocation failure escaping a method documented never to throw.
    /// </summary>
    /// <remarks>
    /// The expectation is <em>dropped</em> rather than refused, because that is what a record this malformed
    /// is: it is not an entry, and reporting it as an entry nobody could read would put a failure about a
    /// file that says nothing about the bin into every listing of it. Only a record that could not be opened
    /// is worth reporting, and that one carries the filesystem's own code.
    /// </remarks>
    private static Task NativeIndexReaderDropsARecordDeclaringAnImpossiblePathLength(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        // A header and nothing after it, declaring the largest character count the reader admits. What the
        // reader does with that count — allocate for it, or overflow adding it — is the whole subject.
        var header = new byte[NativeIndexFile.HeaderLength];
        WriteInt64(header, 0, NativeIndexFile.Version);
        WriteInt64(header, 8, 4096);
        WriteInt64(header, 16, DateTime.UtcNow.ToFileTimeUtc());
        WriteInt32(header, 24, int.MaxValue / 2);

        var path = Path.Combine(ScratchWorkspace.Root, "$Icorrupt.bin");
        File.WriteAllBytes(path, header);

        Check.False(
            NativeIndexFile.TryRead(path, out _, out var hResult),
            "a record declaring a path no Windows path can be is not read as one");

        Check.Equal(
            0,
            hResult,
            $"and it is dropped as malformed rather than reported as an entry nobody could read (got 0x{hResult:X8})");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A record's deletion time is a file time read straight out of the file, and converting one the runtime
    /// cannot express raises. The record is not unreadable for that: it is not a record at all, and a bin
    /// holding one is a bin with one fewer entry in it rather than a bin with a problem in it.
    /// </summary>
    /// <remarks>
    /// The distinction is the one <see cref="NativeIndexFile.TryRead(string, out NativeIndexRecord, out int)"/>
    /// draws between the two things that can go wrong. A record that could not be <em>opened</em> is an entry
    /// somebody else is holding, and a listing needs to hear that one is missing. A record whose contents
    /// make no sense says nothing about the bin, and reporting it would put a failure about a stray file
    /// into every listing that walks past it.
    /// </remarks>
    private static Task NativeIndexReaderDropsARecordWhoseTimestampIsOutOfRange(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var original = Path.Combine(ScratchWorkspace.Root, "unreachable-time.bin");
        var bytes = BuildRecord(original, size: 128, deleted: DateTime.UtcNow, version: NativeIndexFile.Version);

        // Every other field is one this reader accepts, so the timestamp is the only thing wrong with it.
        WriteInt64(bytes, 16, long.MaxValue);

        var path = Path.Combine(ScratchWorkspace.Root, "$Ibadtime.bin");
        File.WriteAllBytes(path, bytes);

        Check.False(
            NativeIndexFile.TryRead(path, out _, out var hResult),
            "a record whose deletion time is not a time is not read as one");

        Check.Equal(
            0,
            hResult,
            $"and it is dropped quietly rather than reported as an entry nobody could read (got 0x{hResult:X8})");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// An entry name this library has already handed out is refused as taken, whatever attributes the file
    /// carrying it ended up with — which is what makes the fresh-name retry reachable at all.
    /// </summary>
    /// <remarks>
    /// The claim under test is a claim about the filesystem, so this is stated as one rather than left
    /// implicit: <c>CREATE_NEW</c> fails on the name being present and reads no attributes at all, so a
    /// read-only entry is refused with <c>ERROR_FILE_EXISTS</c> exactly as a writable one is. The write
    /// side of the runner reads that code to tell a collision from a locked file, and the two codes it
    /// accepts are the only two either operation produces on a taken name — <c>ERROR_FILE_EXISTS</c> from
    /// the create and <c>ERROR_ALREADY_EXISTS</c> from the move.
    /// <para>
    /// Both halves are asserted rather than the create alone, because the move is the one a reader is more
    /// likely to doubt: it also refuses a read-only name with <c>ERROR_ACCESS_DENIED</c> when asked to
    /// overwrite it, and this runner never asks it to. Nothing here creates a file outside the run's own
    /// directory, so the collisions are staged inside it rather than in a real bin.
    /// </para>
    /// </remarks>
    private static Task ATakenEntryNameIsRefusedAsACollisionWhateverItsAttributes(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var taken = Path.Combine(ScratchWorkspace.Root, "$ITAKEN.bin");

        // The attributes a real entry carries, plus read-only on top: the point is that none of them changes
        // what the refusal says.
        File.WriteAllText(taken, "an entry that is already there");
        File.SetAttributes(taken, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

        var refused = Check.Throws<IOException>(
            () => NativeIndexFile.Write(taken, ScratchWorkspace.Root + "another.bin", 0, DateTime.UtcNow),
            "creating a record over a name that is taken is refused rather than overwriting it");

        Check.Equal(
            ErrorFileExists,
            refused.HResult,
            $"and it is refused as a taken name, which is what earns the retry (got 0x{refused.HResult:X8}, win32 {refused.HResult & 0xFFFF})");

        // The other end of the same collision: the payload's move onto a name that is already there.
        var source = Path.Combine(ScratchWorkspace.Root, "moving-source.bin");
        File.WriteAllText(source, "the payload");

        var blocked = Check.Throws<IOException>(
            () => File.Move(source, taken),
            "a move onto a name that is taken is refused too");

        Check.Equal(
            ErrorAlreadyExists,
            blocked.HResult,
            $"and is refused by the second of the two codes the runner reads as a collision (got 0x{blocked.HResult:X8}, win32 {blocked.HResult & 0xFFFF})");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Win32 <c>ERROR_FILE_EXISTS</c>: a create that would overwrite a name.</summary>
    private const int ErrorFileExists = unchecked((int)0x80070050);

    /// <summary>Win32 <c>ERROR_ALREADY_EXISTS</c>: a move that would overwrite a name.</summary>
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    /// <summary>Win32 <c>ERROR_FILE_NOT_FOUND</c>: a move of a source that is not there.</summary>
    private const int ErrorFileNotFound = unchecked((int)0x80070002);

    /// <summary>Win32 <c>ERROR_SHARING_VIOLATION</c>: a move of a file somebody is holding.</summary>
    private const int ErrorSharingViolation = unchecked((int)0x80070020);

    /// <summary>
    /// A refusal carrying a taken name's code is answered as a taken name, and a refusal carrying anything
    /// else is not. That is what lets the same path be given a fresh name without a locked file being given
    /// one instead.
    /// </summary>
    /// <remarks>
    /// The one case that reads <see cref="NameCollision.IsTaken"/>, for both of the paths that call it. They
    /// shared it after each had a copy of its own, and two cases asserting one judgment's answers would be the
    /// same duplication one layer up. Every refusal below is raised by the filesystem rather than written
    /// down by the case.
    /// <list type="bullet">
    /// <item><b>An overwriting move onto a destination that will not open is taken as whatever it is.</b> It
    /// arrives as an <see cref="UnauthorizedAccessException"/>, whose <see cref="Exception.HResult"/> is the
    /// type's own constant rather than a code, so it would read the same whatever the disk had said.</item>
    /// <item>It cannot reach the restore path's branch: <c>Place</c> overwrites only under
    /// <see cref="RestoreAction.Replace"/> and the filter beside it asks only under
    /// <see cref="RestoreAction.Rename"/>.</item>
    /// <item><b>The loop this feeds is not what is witnessed.</b> <see cref="BinStemSequencer"/> starts above
    /// every name in the bin and hands out the next one, so making the runner collide would take another
    /// process writing to the same bin at that instant. What is reached is the decision its two catch filters
    /// make.</item>
    /// </list>
    /// </remarks>
    private static Task ATakenNameIsRecognisedByItsCodeAndALockedFileIsNot(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        Span<char> stem = stackalloc char[NativeBinLayout.StemLength];
        NativeBinLayout.NextStem(stem);

        var indexPath = NativeBinLayout.IndexPath(ScratchWorkspace.Root, stem, ".bin");
        var payloadPath = NativeBinLayout.PayloadPath(ScratchWorkspace.Root, stem, ".bin");

        // Standing where a run that crashed between the two halves would have left them. The runner never sees
        // either name, so nothing here is asking it to land on a name of its own choosing.
        File.WriteAllText(indexPath, "a record that is already there");
        File.WriteAllText(payloadPath, "a payload that is already there");

        var recordRefused = Check.Throws<IOException>(
            () => NativeIndexFile.Write(indexPath, ScratchWorkspace.Root + "original.bin", 0, DateTime.UtcNow),
            "a record cannot be written over a name that is taken");

        var payloadSource = CreateFile("payload-wanted-that-name");
        var payloadRefused = Check.Throws<IOException>(
            () => File.Move(payloadSource, payloadPath),
            "and a payload cannot be moved onto one either");

        var held = CreateFile("held-by-somebody-else");
        IOException holdRefused;

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            holdRefused = Check.Throws<IOException>(
                () => File.Move(held, Path.Combine(ScratchWorkspace.Root, "somewhere-free.bin")),
                "a file somebody is holding cannot be moved at all");
        }

        var absentRefused = Check.Throws<IOException>(
            () => File.Move(Path.Combine(ScratchWorkspace.Root, "never-created.bin"), Path.Combine(ScratchWorkspace.Root, "also-free.bin")),
            "and neither can one that is not there");

        var overwriting = CreateFile("overwritten-into");
        var denied = Path.Combine(ScratchWorkspace.Root, "held-destination.bin");
        File.WriteAllText(denied, "occupied by somebody who will not let go");

        Exception deniedRefused;

        using (new FileStream(denied, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            deniedRefused = Check.Throws<Exception>(
                () => File.Move(overwriting, denied, overwrite: true),
                "an overwriting move into a destination nobody may open is refused");
        }

        Check.Equal(
            ErrorFileNotFound,
            absentRefused.HResult,
            $"a source that is not there arrives as 0x{ErrorFileNotFound:X8} (got 0x{absentRefused.HResult:X8}, win32 {absentRefused.HResult & 0xFFFF})");

        Check.Equal(
            ErrorSharingViolation,
            holdRefused.HResult,
            $"and a file somebody is holding as 0x{ErrorSharingViolation:X8} (got 0x{holdRefused.HResult:X8}, win32 {holdRefused.HResult & 0xFFFF})");

        Check.True(
            NameCollision.IsTaken(recordRefused),
            $"a refusal carrying 0x{recordRefused.HResult:X8} is a name being taken, which is what earns the fresh one");

        Check.True(
            NameCollision.IsTaken(payloadRefused),
            $"and so is one carrying 0x{payloadRefused.HResult:X8}, which arrives from the move rather than from the create");

        Check.False(
            NameCollision.IsTaken(holdRefused),
            $"while 0x{holdRefused.HResult:X8} is a fact about the caller's own file, and no name of this library's own was lost");

        Check.False(
            NameCollision.IsTaken(absentRefused),
            $"and 0x{absentRefused.HResult:X8} is not a name either");

        Check.False(
            NameCollision.IsTaken(deniedRefused),
            $"and 0x{deniedRefused.HResult:X8} on a {deniedRefused.GetType().Name} is not a name either, and its type is not one the judgment reads");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// What was written is what a listing finds. The entry, its original path, its size and
    /// its payload's location all come back.
    /// </summary>
    private static async Task NativeListingFindsAnEntryNativeRecycleRecorded(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the file to be listed out of");

        var path = CreateFile("listed.bin");
        var size = new FileInfo(path).Length;

        var recycled = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken), path);

        Check.Equal(path, item.OriginalPath, "the listing names the path the file was deleted from");
        Check.Equal(size, item.Size, "with the size the file had, which is what the record carried");
        Check.False(item.IsDirectory, "and it is not a directory");
        Check.True(File.Exists(item.PayloadPath), $"its payload is where the listing says ({item.PayloadPath})");

        workspace.Dispose();
    }

    /// <summary>
    /// The one field a record cannot answer. A version 2 record stores zero for a directory and for an
    /// empty file alike, so the only way to tell them apart is to ask the filesystem what is at the payload
    /// path.
    /// </summary>
    private static async Task NativeListingReportsARecycledDirectoryAsADirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the directory to be listed out of");

        var directory = Path.Combine(ScratchWorkspace.Root, "listed-directory-" + RunTag);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "inside.bin"), Content);

        var recycled = await RecycleBin.RecycleAsync([directory], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the directory is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken), directory);

        Check.True(item.IsDirectory, "the listing says it is a directory");
        Check.Equal(0, item.Size, "with the size of zero the record carries for one");
        Check.True(Directory.Exists(item.PayloadPath), $"and the payload is a directory ({item.PayloadPath})");

        workspace.Dispose();
    }

    /// <summary>
    /// A volume on which nothing has ever been recycled has no bin directory at all. That is the ordinary
    /// state of a fresh volume, and it is answered with an empty listing rather than an exception — a caller
    /// asking what is in the bin is asking a question that has an answer.
    /// </summary>
/// <remarks>
    /// Driven through a volume root the run already owns, whose bin directory was never created, rather
    /// than through a volume that is not mounted: what is under test is what the listing does when there
    /// is nothing there to list. Passing the bin directory itself would append <c>$RECYCLE.BIN\&lt;SID&gt;</c>
    /// to it a second time and pass for the wrong reason, so the case asserts the composition first and then
    /// hands the lister the root it composes from.
    /// </remarks>
    private static async Task NativeListingOfAVolumeWithNoBinDirectoryIsEmpty(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var root = Path.Combine(ScratchWorkspace.Root, "volume-with-no-bin") + Path.DirectorySeparatorChar;

        Check.Equal(
            Path.Combine(root, "$RECYCLE.BIN", NativeBinLayout.CurrentSid),
            NativeBinLayout.BinDirectory(root),
            "the bin of a volume is that volume's own directory with the current user's SID under it");

        Check.False(Directory.Exists(NativeBinLayout.BinDirectory(root)), "and here it does not exist");

        var contents = await Task
            .Run(() => NativeBinLister.TryEnumerate([root], GetItemsOptions.Default, context.CancellationToken), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(1, contents.Items.Count, "a volume is answered even when its bin holds nothing");
        Check.Equal(root, contents.Items[0].VolumeRoot, "the group names the volume it was read from");
        Check.Equal(0, contents.Items[0].Items.Count, "and its bin is an empty group rather than an absent one");
        Check.Equal(0, contents.Failures.Count, "and no failures, because an empty bin is an answer rather than a problem");

        workspace.Dispose();
    }

    /// <summary>
    /// Volumes are read in parallel, and a listing answers one group per volume in the order it was asked
    /// for them.
    /// </summary>
    /// <remarks>
    /// The read of a volume is the parallel part — every disk's bin directory is walked on its own thread,
    /// and a machine with three volumes with a bin does three walks at once rather than in a row. What that
    /// must not do is decide the answer's shape: a caller comparing two listings, or a caller matching a
    /// group to the volume it asked about, cannot have the order depend on which thread finished first. Each
    /// volume writes into a slot taken by its position in the list that was handed in, and the groups are
    /// flattened in that same order once every walk is done.
    /// </remarks>
    private static async Task NativeListingGroupsEveryVolumeAndKeepsTheirOrder(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the entries to reach");

        // Two roots that are not volumes at all, standing in for disks that are not there. Each answers with
        // an empty group rather than being refused, because the resolution that produced them already
        // decided they were volumes and refusing here would be a second, different answer to one question.
        var roots = new List<string>
        {
            Path.Combine(ScratchWorkspace.Root, "volume-one") + Path.DirectorySeparatorChar,
            Path.Combine(ScratchWorkspace.Root, "volume-two") + Path.DirectorySeparatorChar,
            Path.Combine(ScratchWorkspace.Root, "volume-three") + Path.DirectorySeparatorChar
        };

        var contents = await Task
            .Run(() => NativeBinLister.TryEnumerate(roots, GetItemsOptions.Default, context.CancellationToken), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(roots.Count, contents.Items.Count, "every volume asked about is answered, whether or not it had a bin");

        for (var i = 0; i < roots.Count; i++)
        {
            Check.Equal(
                roots[i],
                contents.Items[i].VolumeRoot,
                $"the group at position {i} is the volume that was asked about at position {i}, and not whichever walk finished first");
        }

        workspace.Dispose();
    }

    /// <summary>
    /// A record whose payload is gone — a crash between the two writes, or an empty that ran while a listing
    /// was in flight — is not an entry a caller can be told about. There is no file left to report a size
    /// for, and its record is litter the next empty collects, not a row in a listing.
    /// </summary>
    /// <remarks>
    /// What the empty does with such a record is not asserted here, and deliberately so: this case is about
    /// what a listing says, and emptying the volume's whole Recycle Bin to find out would cost the run
    /// seconds of disk and change what every later case sees on this volume.
    /// </remarks>
    private static async Task NativeListingHidesAnIndexWhosePayloadIsGone(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the entry to be left half-made in");

        var path = CreateFile("orphan.bin");
        var recycled = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var indexPath = RecordOf(path);
        var payloadPath = NativeBinLayout.ToPayloadPath(indexPath);
        Check.True(File.Exists(payloadPath), "the payload is there to begin with");

        File.Delete(payloadPath);

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.False(
            Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).Any(i => string.Equals(i.OriginalPath, path, StringComparison.OrdinalIgnoreCase)),
            "the entry is not listed, because there is no payload left to say what it is");
        Check.Equal(0, contents.Failures.Count, "and an entry with nothing behind it is not a failure either");
        Check.True(File.Exists(indexPath), "while its record is still on the volume, for the next empty to collect");

        workspace.Dispose();
    }

    /// <summary>
    /// A listing that reads no records cannot be disturbed by one nobody can read. Locking an entry's
    /// record used to cost a caller a failure line; now the listing never opens it, and the entry is
    /// returned like any other — with its details waiting for whoever asks.
    /// </summary>
    private static async Task NativeListingIsUndisturbedByARecordItCannotRead(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin holding an entry the case can lock");

        var locked = CreateFile("locked-listing.bin");
        var other = CreateFile("readable-listing.bin");

        var recycled = await RecycleBin.RecycleAsync([locked, other], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"both are recycled (first failure: {Describe(recycled.FirstFailure)})");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);
        var baseline = Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).Count;

        using (new FileStream(RecordOf(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);
        }

        Check.Equal(0, contents.Failures.Count, "no record is opened, so none of them can be unreadable");
        Check.Equal(baseline, Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).Count, "and every entry is returned, the locked one included");

        workspace.Dispose();
    }

    /// <summary>
    /// The drive is classified by asking it, and the answer decides how many threads read its bin.
    /// </summary>
    /// <remarks>
    /// Nothing here knows what any particular machine has, and the case does not assume an answer is
    /// available: a flash drive's driver never implemented the seek-penalty property, and <c>Unknown</c> is
    /// the correct answer for it rather than a failure. What is asserted is the mapping — every kind of
    /// answer reaches the right degree, and an absent answer reaches the safe one — because that is the part
    /// that holds on every volume.
    /// </remarks>
    private static Task ADriveIsClassifiedFromItsOwnSeekPenaltyAnswer(TestContext context)
    {
        var root = ScratchWorkspace.VolumeRoot;
        var kind = VolumeSeekPenalty.Classify(root);

        Check.True(
            kind is VolumeStorageKind.Unknown or VolumeStorageKind.Mechanical or VolumeStorageKind.SolidState,
            $"the classifier answers with one of its three kinds rather than something else ({kind})");

        var cores = Environment.ProcessorCount;

        var expected = kind switch
        {
            VolumeStorageKind.SolidState => Math.Min(cores, VolumeSeekPenalty.SolidStateReadDegreeCap),
            VolumeStorageKind.Mechanical => Math.Min(cores * 2, VolumeSeekPenalty.MechanicalReadDegreeCap),
            _ => 1
        };

        Check.Equal(
            expected,
            VolumeSeekPenalty.DegreeOfParallelism(root),
            kind == VolumeStorageKind.SolidState
                ? "a solid-state volume is read on one thread per core, capped where a cold sweep stopped improving — measured 18.0 s at six threads, 16.1 s at eight, and 16.1 s again at sixteen"
                : kind == VolumeStorageKind.Mechanical
                    ? "a mechanical volume is read on two threads per core, but capped at eight rather than sixteen: past eight the curve falls away, because each further request in flight is another seek — measured 7.7 s at eight against 9.6 s at sixteen over three passes each"
                    : "a volume nothing answered for is read on one thread, because its cost is network latency and multiplying that does not reduce it");

        Check.Equal(kind, VolumeSeekPenalty.Classify(root), "and the answer is the same when asked twice, because the hardware does not change under a run");

// A share is answered without asking anything: \\.\ resolves against this machine, so there is no
        // volume device to ask, and a network's cost is latency rather than a seek.
        Check.Equal(
            VolumeStorageKind.Unknown,
            VolumeSeekPenalty.Classify(@"\\server\share\"),
            "a share is unknown, and is not asked a question whose device path would name the local machine");

        // Composed but not touched. Listing a share would be a network round trip for a case that has no
        // share to name and no business reaching one: what is checked here is that the composition produces
        // the shape the lister would then look for, and the lister's answer for a directory that is not there
        // is the case above.
        Check.Equal(
            @"\\server\share\$RECYCLE.BIN\" + NativeBinLayout.CurrentSid,
            NativeBinLayout.BinDirectory(@"\\server\share\"),
            "a share's bin is composed the same way a volume's is, under the share root");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Two volumes that are partitions of one disk share a group, because a group is a disk and work
    /// inside a group contends for one head. A volume that named no disk goes into one group with every
    /// other such volume rather than a group of its own.
    /// </summary>
    /// <remarks>
    /// The partition number the driver answers with is left out of the key, which is the whole of why two
    /// volumes on one disk land together: a key carrying the partition would put every volume in a group
    /// of its own, and the grouping would answer the same thing on a machine that has nothing to group.
    /// <para>
    /// The shares are the other half of the rule. Neither named a disk, so neither may be assumed to have
    /// the machine to itself, and a group each would have them run together — the answer that looks faster
    /// and is a guess in the direction of contention. The group that names no disk is last whatever order
    /// the roots arrived in, so a caller comparing two runs sees the same shape.
    /// </para>
    /// <para>
    /// The volumes are asked of the machine rather than written down, for the reason
    /// <see cref="VolumePresence"/> gives: which volumes share a disk is a fact about the machine, and a
    /// letter written into this case would assert one machine's partition layout rather than the rule.
    /// <see cref="VolumePresence.RequireASameDisk"/> and <see cref="VolumePresence.RequireADifferentDisk"/>
    /// each skip rather than fail when the machine cannot supply the shape the claim needs, so a run on a
    /// machine with one volume per disk reports that it did not run here instead of going red over a
    /// layout it never chose.
    /// </para>
    /// </remarks>
    private static Task VolumesAreGroupedByTheDiskTheyArePartitionsOf(TestContext context)
    {
        VolumePresence.RequireASameDisk(out var sameDisk, "two volumes sharing one physical disk");
        VolumePresence.RequireADifferentDisk(out var otherDisk, "a volume on a physical disk of its own");

        var groups = VolumeDiskGroups.Group(
        [
            ScratchWorkspace.VolumeRoot,
            AbsentVolume.RootOf(sameDisk),
            AbsentVolume.RootOf(otherDisk),
            @"\\server\share\",
            @"\\other\share\"
        ]);

        Check.Equal(3, groups.Count, "two physical disks behind the volumes, and one group for the shares");

        Check.Equal(2, groups[0].Positions.Count, "the two volumes on one physical disk share a group");
        Check.Equal(0, groups[0].Positions[0], "a group keeps the order the roots arrived in");
        Check.Equal(1, groups[0].Positions[1], "and the second root is second in it");
        Check.True(groups[0].Disk is not null, "a group of volumes that named their disk names it");

        Check.Equal(1, groups[1].Positions.Count, "the volume on the other disk is alone on it");
        Check.Equal(2, groups[1].Positions[0], "at the position it was passed at");
        Check.True(groups[1].Disk is not null, "and its group names that disk");

        Check.True(groups[2].Disk is null, "the shares share one group, and it names no disk");
        Check.Equal(2, groups[2].Positions.Count, "because both of them are in it");
        Check.Equal(3, groups[2].Positions[0], "each keeping the place it was passed at");
        Check.Equal(4, groups[2].Positions[1], "and its own");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Two volumes on two disks are emptied at the same time, and the answer still names them in the order
    /// they were asked for rather than in the order the threads finished.
    /// </summary>
    /// <remarks>
    /// This is the only thing a caller can observe about the order, and it is the reason each volume writes
    /// a slot of its own: <c>VolumesEmptied</c> is read as "which volumes are empty now", and a list built
    /// by appending would answer in the order the devices happened to be free.
    /// <para>
    /// <b>Two disks, not two volumes.</b> Invented roots all land in the unnamed group — a root inside the
    /// working directory is not a volume, so the driver declines to name a disk for it — and four of them
    /// make one group, where the parallel loop never runs and the case proves nothing. The guard asks the
    /// machine for a volume on a disk the run's volume is not on, and skips rather than falling back.
    /// </para>
    /// <para>
    /// The order is asserted by position in the list rather than by an index into the argument: the two are
    /// the same thing only while the list is right, which is the point.
    /// </para>
    /// </remarks>
    private static async Task AnEmptyAcrossTwoDisksAnswersInTheOrderTheyWereAskedFor(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("a Recycle Bin to empty across two disks");

        VolumePresence.RequireADifferentDisk(out var second, "two disks emptied at once to answer in an order");

        // The second volume deliberately comes first, so an answer built by appending cannot match the
        // argument by accident even when both volumes finish immediately.
        var secondRoot = AbsentVolume.RootOf(second);
        var firstRoot = ScratchWorkspace.VolumeRoot;

        await RecycleBin.EmptyAsync(secondRoot, context.CancellationToken).ConfigureAwait(false);
        await RecycleBin.EmptyAsync(firstRoot, context.CancellationToken).ConfigureAwait(false);

        var emptied = NativeBinEmptier.Empty([secondRoot, firstRoot], context.CancellationToken);

        Check.Equal(2, emptied.VolumesEmptied.Count, $"both volumes are reported empty (got {string.Join(", ", emptied.VolumesEmptied)})");
        Check.Equal(secondRoot, emptied.VolumesEmptied[0], "the one named first is reported first");
        Check.Equal(firstRoot, emptied.VolumesEmptied[1], "and the one named second is reported second, whichever disk was free first");

        workspace.Dispose();
    }

    /// <summary>
    /// Naming the same volume twice empties it once, and counts what it removed once.
    /// </summary>
    /// <remarks>
    /// The public API cannot produce this — <see cref="RecycleBin.EmptyAsync"/> resolves either one volume or
    /// every mounted one, and neither can name the same root twice — so what is held here is the internal
    /// engine's own answer, and what a caller would be harmed by is the counters rather than the list: an
    /// entry that was removed would otherwise be added to <see cref="RecycleBinEmptyResult.ItemsRemoved"/>
    /// and <see cref="RecycleBinEmptyResult.BytesFreed"/> twice.
    /// </remarks>
    private static async Task AnEmptyOfTheSameVolumeTwiceDoesNotCountItTwice(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries to be counted once rather than twice");

        const int fileSize = 4096;
        var files = workspace.CreateRandomFiles(3, fileSize);

        await RecycleBin.RecycleAsync(files, RecycleOptions.Default, cancellationToken: context.CancellationToken).ConfigureAwait(false);

        var emptied = NativeBinEmptier.Empty([ScratchWorkspace.VolumeRoot, ScratchWorkspace.VolumeRoot], context.CancellationToken);

        Check.Equal(0, emptied.Failures.Count, $"nothing failed ({string.Join("; ", emptied.Failures.Select(f => f.Path))})");
        Check.Equal(3, emptied.ItemsRemoved, "the three entries are counted once, by the pass that removed them");
        Check.Equal(3L * fileSize, emptied.BytesFreed, "and so are their bytes");

        workspace.Dispose();
    }

    /// <summary>
    /// An empty cancelled before it starts raises <see cref="OperationCanceledException"/>, and not an
    /// <see cref="AggregateException"/>.
    /// </summary>
    /// <remarks>
    /// The aggregation is the one thing here that is new. Volumes now run on threads the caller's token
    /// also rides on, and an exception out of a loop over threads is what arrives as an
    /// <see cref="AggregateException"/>. It does not, and the reason is worth pinning: the loop throws
    /// through an <see cref="OperationCanceledException"/> carrying the token from its
    /// <see cref="ParallelOptions"/>, and a cancellation carrying <em>any other</em> token is aggregated.
    /// Checked on .NET 10 rather than taken on trust, since the two share an exception type and differ only
    /// in which token it carries.
    /// <para>
    /// This calls the engine rather than <see cref="RecycleBin.EmptyAsync"/>, and on purpose. The public
    /// method checks its own token before it resolves any volume, so a cancelled token never reaches the
    /// loop at all — which is the right behaviour and leaves this path for
    /// <see cref="NativeEmptyHonoursCancellationBetweenEntries"/> to hold. What is untested without a direct
    /// call is the only thing that is new.
    /// </para>
    /// <para>
    /// Two disks, because one disk takes the branch that runs inline and never enters a loop at all. Cancelling
    /// before the call is the only form a case can make deterministic: stopping a sweep part-way would have to
    /// be timed, and a timed assertion on how far a parallel loop got is a flaky assertion about the machine's
    /// load. What this holds is the shape of the raise, not the point of the stop.
    /// </para>
    /// </remarks>
    private static async Task AnEmptyCancelledBeforeItStartsRaisesAndDoesNotAggregate(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        VolumePresence.RequireADifferentDisk(out var second, "two disks for the parallel loop to refuse work on");

        using var cancelling = new CancellationTokenSource();
        await cancelling.CancelAsync().ConfigureAwait(false);

        var thrown = Check.Throws<OperationCanceledException>(
            () => NativeBinEmptier.Empty([AbsentVolume.RootOf(second), ScratchWorkspace.VolumeRoot], cancelling.Token),
            "an empty whose token is already cancelled raises rather than spreading the loop's exception over an aggregate");

        Check.Equal(cancelling.Token, thrown.CancellationToken, "carrying the token the caller cancelled, unwrapped");

        workspace.Dispose();
    }

    /// <summary>
    /// The counts an empty reports are exact rather than inferred: the sizes already written into the
    /// records being deleted are summed, so the totals have to match what those records say or something
    /// removed the wrong thing.
    /// </summary>
    /// <remarks>
    /// The bin is emptied first so that the case measures its own operation rather than whatever the machine
    /// happened to be holding, and the expected totals are computed from the records themselves — which is
    /// the same source the implementation uses, so agreement here says the sizes were carried through rather
    /// than that two numbers happened to match.
    /// </remarks>
    private static async Task NativeEmptyRemovesWhatWasRecycledAndReportsExactCounts(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the files to be counted and removed from");

        await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

        const int fileSize = 8192;
        var files = CreateMany(5, sizeBytes: fileSize);
        var recycled = await RecycleBin.RecycleAsync(files, RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"every file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var records = IndexRecords();
        var totalBytes = 0L;
        var counted = 0;
        foreach (var path in records)
        {
            if (NativeIndexFile.TryRead(path, out var record))
            {
                counted++;
                totalBytes += record.Size;
            }
        }

        Check.Equal(files.Count, counted, $"every recycled file left a record ({records.Count} records found)");

        var emptied = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, emptied.Failures.Count, $"nothing failed ({string.Join("; ", emptied.Failures.Select(f => f.Path))})");
        Check.Equal(counted, emptied.ItemsRemoved, $"every record that was there is removed and named ({counted})");
        Check.Equal(totalBytes, emptied.BytesFreed, "and so is every byte of them, exactly");
        Check.Equal(fileSize * files.Count, emptied.BytesFreed, $"which for {files.Count} files of {fileSize} bytes is {fileSize * files.Count}");
        Check.Equal(0, IndexRecords().Count, "and no record survives");
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot), "the bin is empty afterwards");

        workspace.Dispose();
    }

    /// <summary>
    /// Emptying an empty bin is what a caller does to be sure, and it must not be reported as work done.
    /// </summary>
    private static async Task NativeEmptyOfAVolumeWithNothingInItReportsNothing(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin to empty twice");

        var first = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);
        var second = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, second.ItemsRemoved, "a second empty removes nothing");
        Check.Equal(0, second.BytesFreed, "and frees nothing");
        Check.Equal(0, second.Failures.Count, "with nothing to report, because nothing went wrong");

        workspace.Dispose();
    }

    /// <summary>
    /// A call with no volumes named is a call that did nothing, and it must reach that answer rather than
    /// tripping over the absence of a slot to write into.
    /// </summary>
    /// <remarks>
    /// The grouping is what makes this reachable at all: zero volumes name zero disks, so the caller's own
    /// guard — "one disk runs inline, several run together" — has no branch that fits, and both of the ones
    /// that exist must be skipped rather than entered with index zero of nothing.
    /// </remarks>
    private static Task NativeEmptyOfNoVolumesIsAnEmptyResult(TestContext context)
    {
        var emptied = NativeBinEmptier.Empty([], context.CancellationToken);

        Check.Equal(0, emptied.ItemsRemoved, "nothing was removed, because nothing was named");
        Check.Equal(0, emptied.BytesFreed, "and nothing was freed");
        Check.Equal(0, emptied.VolumesEmptied.Count, "no volume is reported as emptied");
        Check.Equal(0, emptied.Failures.Count, "and none failed, because there was no work to fail at");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The bin directories themselves are the user's, not the contents'. Emptying removes what is inside the
    /// current user's directory on this volume; it does not remove the directory, the <c>$RECYCLE.BIN</c>
    /// folder above it, or anything belonging to another account.
    /// </summary>
    private static async Task NativeEmptyKeepsTheBinDirectoriesThemselves(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin whose directories must survive being emptied");

        var path = CreateFile("survivor.bin");
        var recycled = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

        var binDirectory = BinDirectory;
        Check.True(Directory.Exists(binDirectory), $"this user's bin directory survives ({binDirectory})");
        Check.True(Directory.Exists(Path.Combine(ScratchWorkspace.VolumeRoot, "$RECYCLE.BIN")), "and so does the folder above it");

        workspace.Dispose();
    }

    /// <summary>
    /// An empty whose token is already cancelled raises rather than proceeding, so no operation runs and no
    /// result is returned to describe one.
    /// </summary>
    /// <remarks>
    /// The title this case carried claimed a cancellation <em>between</em> entries and said the counts had to
    /// report how far it got. The body does neither: the token is cancelled before the call, so the refusal
    /// comes from <see cref="RecycleBin.EmptyAsync"/>'s own prologue, and the case has no counts to read.
    /// What it pins is the raise, and nothing about a sweep that stops part way.
    /// </remarks>
    private static async Task NativeEmptyHonoursCancellationBetweenEntries(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries for a cancelled empty to leave alone");

        var files = CreateMany(3, sizeBytes: 4096);
        var recycled = await RecycleBin.RecycleAsync(files, RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"every file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        // Taken after the recycle rather than before it, so this counts what the case put in the bin and not
        // what the machine already held — the same reason RestoreSuite.BaselineCount takes it before anything
        // is recycled, read here as the count to compare against rather than as a baseline to subtract.
        var before = RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        await Check.ThrowsAsync<OperationCanceledException>(
            () => RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, cancelled.Token),
            "an empty whose token is already cancelled raises rather than proceeding");

        Check.Equal(
            before,
            RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot),
            "and the bin is untouched, because the refusal comes before any work");

        workspace.Dispose();
    }


    /// <summary>
    /// The claim behind all of it: an item this backend recycled can be put back where it came from, byte
    /// for byte, by following the record it wrote. That is what the Explorer's Restore does, and it is the
    /// reason the record is written in Windows' own format rather than one this library reads.
    /// </summary>
    private static async Task ARecycledItemRestoresFromItsRecordedPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an item to be restored out of");

var path = CreateDeepFile(PastMaxPath);
        var original = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);

        var recycled = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"a long path recycles (first failure: {Describe(recycled.FirstFailure)})");

        // Restoring means exactly what Explorer's Restore means: move the payload back to the path the
        // record names. Nothing here knows the format — it reads the record and obeys it.
        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken), path);

        Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
        File.Move(item.PayloadPath, item.OriginalPath);

        // The last component only. A deep path in a failure message is a line no one can read and a length
        // this suite is not meant to be printing, and the assertion is about the bytes either way.
        Check.True(File.Exists(path), $"the payload is back where it was deleted from ({Path.GetFileName(path)}, {path.Length} characters)");

        var restored = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);
        Check.True(original.AsSpan().SequenceEqual(restored),
            "with every byte of it, on a path longer than 260 characters");

        workspace.Dispose();
    }

    /// <summary>
    /// A caller-supplied options object is not obliged to have been through the clamp, and an attempt budget
    /// of zero would otherwise run no rounds at all: the batch would report that it recycled nothing and
    /// failed nothing, which is indistinguishable from an empty batch and is neither true.
    /// </summary>
    private static async Task AnUnclampedAttemptBudgetStillRecycles(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the file to reach");

        var path = CreateFile("zero-attempts.bin");
        var options = new RecycleOptions { MaxAttempts = 0 };

        var result = await RecycleBin.RecycleAsync([path], options, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the file is recycled anyway, because zero attempts clamps to one (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(1, result.RecycledPaths.Count, "and is named as recycled rather than silently dropped");
        Check.False(File.Exists(path), "so it really is in the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// <see cref="RecycleOptions.OverallTimeout"/> bounds the retrying of a whole batch, not each round's
    /// wait taken on its own. A deadline recomputed before every backoff is a fresh budget every time, so the
    /// loop runs to <see cref="RecycleOptions.MaxAttempts"/> whenever each individual delay fits — which is
    /// the case a caller setting a budget is exactly the one that hits.
    /// </summary>
    /// <remarks>
    /// The numbers are chosen so the jitter cannot move the answer. Delays double from 30 ms, so the three
    /// before the last add to at most 252 ms against a 300 ms budget — round four is always reached — and
    /// the fourth is 240 ms at its lowest against whatever is left of the budget, which is at most 252 ms
    /// spent and therefore at least 48 ms remaining. A deadline taken once gives four attempts; one taken
    /// afresh each time gives five, because 240 ms still fits inside a whole 300 ms budget.
    /// </remarks>
    private static async Task NativeOverallTimeoutBoundsTheWholeRetryBudget(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the batch to be measured against");

        var locked = CreateFile("budget-locked.bin");

        var options = new RecycleOptions
        {
            MaxAttempts = 20,
            InitialRetryDelay = TimeSpan.FromMilliseconds(30),
            OverallTimeout = TimeSpan.FromMilliseconds(300)
        };

        RecycleBinResult result;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await RecycleBin.RecycleAsync([locked], options, context.CancellationToken).ConfigureAwait(false);
        }

        Check.False(result.Succeeded, "a file that cannot be moved is still reported as a failure");
        Check.NotNull(result.FirstFailure, "and the failure is reported");
        Check.Equal(
            4,
            result.FirstFailure!.Attempts,
            "the budget is spent across the rounds rather than restarted by each of them, so the fifth never starts");
        Check.True(File.Exists(locked), "and the caller's file is left where they put it");

        workspace.Dispose();
    }

    /// <summary>
    /// The clamp that keeps the retry deadline from overflowing exists because adding an elapsed time to a
    /// budget close to <see cref="TimeSpan.MaxValue"/> throws — and it threw from an operation documented
    /// never to throw. A caller is free to set that budget; the library has to survive it.
    /// </summary>
    /// <remarks>
    /// The batch holds a locked file as well as a movable one, because the deadline is only added when a
    /// retry is about to be scheduled. A batch that succeeds first time never reaches it, and a case that
    /// proved nothing would pass either way.
    /// </remarks>
    private static async Task AnAbsurdOverallTimeoutDoesNotOverflow(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the batch to be measured against");

        var locked = CreateFile("absurd-timeout-locked.bin");
        var free = CreateFile("absurd-timeout-free.bin");
        var options = new RecycleOptions
        {
            OverallTimeout = TimeSpan.MaxValue,
            InitialRetryDelay = TimeSpan.Zero
        };

        RecycleBinResult result;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await RecycleBin.RecycleAsync([locked, free], options, context.CancellationToken).ConfigureAwait(false);
        }

        Check.Equal(1, result.Failures.Count, "the locked file is the one that fails");
        Check.True(result.RecycledPaths.Count == 1, $"and the movable one still went ({result.RecycledPaths.Count} recycled)");
        Check.False(File.Exists(free), "so the operation completed rather than throwing part way through it");
    }

    /// <summary>
    /// The invariant the whole record-first ordering exists to protect: a record never outlives its payload.
    /// An entry with a record and no payload is a phantom a user can try and fail to restore; an entry with a
    /// payload and no record is a file on disk that nothing lists, nothing empties and nobody finds. Every
    /// recycle is checked against this after the fact rather than only in the code that writes the record.
    /// </summary>
    private static async Task ARecycledEntryNeverHasItsRecordWithoutItsPayload(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin whose entries must stay in pairs");

        var files = CreateMany(4, sizeBytes: 2048);
        var recycled = await RecycleBin.RecycleAsync(files, RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"every file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        foreach (var original in files)
        {
            var payload = NativeBinLayout.ToPayloadPath(RecordOf(original));

            Check.True(File.Exists(payload) || Directory.Exists(payload),
                $"the record of {original} has its payload beside it ({payload})");
        }
    }

    /// <summary>
    /// A directory is moved whole, and the deep chain is the shape that would break: the record holds the
    /// whole path, but the payload is the directory itself and the move has to reach down every level.
    /// </summary>
    private static async Task ADeepDirectoryMovesWhole(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for a deep directory to be moved into");

        const int depth = 6;
        var component = "level-" + RunTag + "-" + new string('l', 40);

        // Nested one level at a time, then the OUTERMOST of them is what gets recycled — so the move has to
        // carry five more levels underneath it. Recycling the innermost instead would move one directory
        // with one file in it, which is the shallow case the other case already covers.
        var outer = Path.Combine(ScratchWorkspace.Root, component + "-0");

        for (var level = 0; level < depth; level++)
        {
            var levelDirectory = Path.Combine(
                ScratchWorkspace.Root,
                Enumerable.Range(0, level + 1).Select(i => component + "-" + i).Aggregate(Path.Combine));

            Directory.CreateDirectory(levelDirectory);
            File.WriteAllBytes(Path.Combine(levelDirectory, $"inside-{level}.bin"), Content);
        }

        var innermost = Path.Combine(
            ScratchWorkspace.Root,
            Enumerable.Range(0, depth).Select(i => component + "-" + i).Aggregate(Path.Combine));

        Check.True(innermost.Length > 300, $"the innermost path is genuinely deep ({innermost.Length} characters)");
        Check.True(File.Exists(Path.Combine(innermost, $"inside-{depth - 1}.bin")), "and its file is on it before the move");

        var result = await RecycleBin.RecycleAsync([outer], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, "a deep directory is recycled as one entry");
        Check.False(Directory.Exists(outer), "and the outermost of it is gone from disk");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken), outer);

        Check.True(item.IsDirectory, "the listing reports it as a directory");
        Check.True(Directory.Exists(item.PayloadPath), $"whose whole tree came with it ({item.PayloadPath})");

        // Only ever the last component of anything found: the payload sits under a path this suite must not
        // print, and a recursive enumeration of a path this deep would be as much a violation as it would be
        // a long-path operation. What is checked is that the nesting survived the move intact.
        var walk = item.PayloadPath;

        for (var level = 0; level < depth - 1; level++)
        {
// Each level holds exactly two things: the file written into it, and the level below it.
            var entries = Directory.GetFileSystemEntries(walk);
            Check.Equal(2, entries.Length, $"level {level} holds its own file and the level below it");

            Check.True(File.Exists(Path.Combine(walk, $"inside-{level}.bin")), $"and the file written into it came with the move");

            var found = Path.GetFileName(entries.Single(e => (File.GetAttributes(e) & FileAttributes.Directory) != 0));
            Check.Equal(component + "-" + (level + 1), found, $"and the directory below it is the one written at level {level + 1}");

            walk = Path.Combine(walk, found);
        }

var deepestEntries = Directory.GetFileSystemEntries(walk);
        Check.Equal(1, deepestEntries.Length, "the innermost level holds only its own file");
        Check.Equal($"inside-{depth - 1}.bin", Path.GetFileName(deepestEntries[0]), "which is the one written there before the move");

        workspace.Dispose();
    }

    /// <summary>
    /// A volume is named as emptied only when nothing on it was left behind. A bin that kept a locked entry
    /// is not empty, and a caller reading the list is asking whether that volume is empty now.
    /// </summary>
    private static async Task AnEmptyNamesOnlyTheVolumesItActuallyEmptied(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin holding one entry that cannot be removed");

        var stuck = CreateFile("stuck.bin");
        var free = CreateFile("removable.bin");

        var recycled = await RecycleBin.RecycleAsync([stuck, free], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, "both are recycled");

        // The payload is locked where it now lives, after the move. Holding it with no delete share is what
        // makes one entry undeletable without the case having to put the file in the bin in a broken state.
        using (new FileStream(NativeBinLayout.ToPayloadPath(RecordOf(stuck)), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var emptied = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

            Check.True(emptied.VolumesEmptied.Count == 0,
                $"a volume that kept something is not named as emptied (named: {string.Join(", ", emptied.VolumesEmptied)})");
            Check.True(emptied.Failures.Count >= 1, "and the entry that stayed is named");
        }
    }

    /// <summary>
    /// The other side of the same rule: a volume whose bin holds nothing is empty afterwards, and saying so
    /// is the answer to "did you empty it".
    /// </summary>
    private static async Task ABinWithNothingInItIsStillNamedAsEmptied(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin to empty twice");

        await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);
        var second = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(1, second.VolumesEmptied.Count, "a volume with nothing left in it was emptied, and is named");
        Check.True(second.VolumesEmptied.Contains(ScratchWorkspace.VolumeRoot), $"by the root the caller named ({ScratchWorkspace.VolumeRoot})");
        Check.Equal(0, second.Failures.Count, "with nothing to report");
    }

    /// <summary>
    /// Creates files of one size each, in the scratch root.
    /// </summary>
    private static IReadOnlyList<string> CreateMany(int count, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        new Random(20261010).NextBytes(content);

        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(ScratchWorkspace.Root, $"batch-{RunTag}-{i}.bin");
            File.WriteAllBytes(path, content);
            paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// Builds an index record by hand, in the given version, for a reader to be pointed at.
    /// </summary>
    private static byte[] BuildRecord(string originalPath, long size, DateTime deleted, int version)
    {
        if (version == 1)
        {
            var legacy = new byte[544];
            WriteInt64(legacy, 0, 1);
            WriteInt64(legacy, 8, size);
            WriteInt64(legacy, 16, deleted.ToFileTimeUtc());
            System.Text.Encoding.Unicode.GetBytes(originalPath, legacy.AsSpan(24));
            return legacy;
        }

        var characters = originalPath.Length + 1;
        var modern = new byte[28 + (characters * 2)];
        WriteInt64(modern, 0, version);
        WriteInt64(modern, 8, size);
        WriteInt64(modern, 16, deleted.ToFileTimeUtc());
        WriteInt32(modern, 24, characters);
        System.Text.Encoding.Unicode.GetBytes(originalPath, modern.AsSpan(28));
        return modern;
    }

    private static void WriteInt64(byte[] buffer, int offset, long value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), value);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), value);
    }

    /// <summary>
    /// The one entry in a listing that names <paramref name="originalPath"/>, or a failure saying there was
    /// none.
    /// </summary>
    private static RecycleBinItem FindListed(Task<RecycleBinContents> listing, string originalPath)
    {
        var contents = listing.GetAwaiter().GetResult();
        var listed = Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot);
        var matches = listed
            .Where(i => string.Equals(i.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Check.Equal(1, matches.Length, $"exactly one listed entry names {originalPath} (found {matches.Length} of {listed.Count})");

        return matches[0];
    }

    /// <summary>
    /// The write path is refused below Windows 10 build 14393, which is the first build that reads a
    /// version 2 record. Nothing can make this machine report an older version, so what is checked here is
    /// the half that a test on this machine can reach: that the machine satisfies the boundary, and that
    /// the guard therefore lets the write through.
    /// </summary>
    private static Task NativeWriteSupportIsAvailableOnThisMachine(TestContext context)
    {
        var version = RecycleBinBackendGuard.RunningVersion;

        Check.True(
            version.Major > 10 || (version.Major == 10 && version.Build >= RecycleBinBackendGuard.MinimumBuild),
            $"this machine satisfies the boundary the write path is gated on (reported {version.Major}.{version.Minor} build {version.Build}, need 10.0 build {RecycleBinBackendGuard.MinimumBuild} or later)");

        RecycleBinBackendGuard.RequireWriteSupport();

        return Task.CompletedTask;
    }

    /// <summary>
    /// What a listing is built from: one walk of the bin directory that pairs each entry's record with its
    /// payload and says what the payload is. Size and directory-ness come from the directory walk itself,
    /// which is the whole reason a listing does not read a record to learn them.
    /// </summary>
    private static async Task NativeEnumerationPairsIndexAndPayload(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an entry to be enumerated out of");

        var path = CreateFile("enumerated.bin");
        var directory = Path.Combine(ScratchWorkspace.Root, "enumerated-directory-" + RunTag);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "inside.bin"), Content);

        var recycled = await RecycleBin
            .RecycleAsync([path, directory], RecycleOptions.Default, context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"both are recycled (first failure: {Describe(recycled.FirstFailure)})");

        var indexPath = RecordOf(path);
        var entries = NativeBinEntries.Collect(BinDirectory, context.CancellationToken);

        var entry = entries.SingleOrDefault(e => string.Equals(e.IndexPath, indexPath, StringComparison.OrdinalIgnoreCase));
        Check.NotNull(entry, $"the enumeration pairs the entry's record ({entries.Count} pair(s) in the bin)");
        Check.Equal(
            NativeBinLayout.ToPayloadPath(indexPath),
            entry!.PayloadPath,
            "and names the payload the record's own name gives, without looking it up");
        Check.Equal((long)Content.Length, entry.PayloadLength, "with the payload's length taken from the walk");
        Check.False(entry.PayloadIsDirectory, "and a file reported as a file");

        var directoryIndex = RecordOf(directory);
        var directoryEntry = entries.SingleOrDefault(e => string.Equals(e.IndexPath, directoryIndex, StringComparison.OrdinalIgnoreCase));

        Check.NotNull(directoryEntry, "a recycled directory is enumerated as an entry too");
        Check.True(directoryEntry!.PayloadIsDirectory, "and is reported as a directory from the walk alone");

        workspace.Dispose();
    }

    /// <summary>
    /// The point of the whole change: a listing that never opens a record. An eighteen-thousand-entry bin
    /// costs one walk rather than eighteen thousand reads, so a caller who wants only sizes and flags is not
    /// charged for the paths nobody asked for.
    /// </summary>
    /// <remarks>
    /// The record is held exclusively for the duration of the listing, which is the one thing a lazy listing
    /// cannot survive: if it opened the record, it would report the entry as unreadable and the case would
    /// see that failure instead of a clean listing.
    /// </remarks>
    private static async Task NativeListingDoesNotReadIndexRecords(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin holding a record the case can lock");

        var path = CreateFile("unread-record.bin");
        var size = new FileInfo(path).Length;

        var recycled = await RecycleBin.RecycleAsync([path], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var payloadPath = NativeBinLayout.ToPayloadPath(RecordOf(path));
        RecycleBinContents contents;

        using (new FileStream(RecordOf(path), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);

            // Found by its payload rather than by its original path, because asking every entry for its path
            // is the very reading this case says the listing does not do.
            var item = Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).FirstOrDefault(i => SamePath(i.PayloadPath, payloadPath));
            Check.NotNull(item, "the entry is listed even though its record is held open");
            Check.Equal(size, item!.Size, "with the size the walk gave, which never needed the record");
            Check.False(item.IsDirectory, "and it is reported as a file");
        }

        Check.Equal(0, contents.Failures.Count, "and nothing is reported as unreadable, because nothing was opened");

        workspace.Dispose();
    }

    /// <summary>
    /// Asking for the records up front brings the old behaviour back, failures included: a record nobody can
    /// read costs a caller the entry and gets a line in the failures rather than a row that raises later.
    /// </summary>
    private static async Task NativePreloadReportsAnIndexItCannotRead(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin holding a record the case can lock");

        var locked = CreateFile("preload-locked.bin");
        var other = CreateFile("preload-readable.bin");

        var recycled = await RecycleBin.RecycleAsync([locked, other], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"both are recycled (first failure: {Describe(recycled.FirstFailure)})");

        var preload = new GetItemsOptions { PreloadItemDetails = true };
        var baseline = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);

        RecycleBinContents contents;

        using (new FileStream(RecordOf(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, preload, context.CancellationToken).ConfigureAwait(false);
        }

        Check.Equal(1, contents.Failures.Count, "exactly one entry is reported as unreadable");
        Check.Equal(RecycleFailureStage.Enumeration, contents.Failures[0].Stage, "at the stage where a listing reports what it could not read");
        Check.Equal(Listing.Flat(baseline).Count - 1, Listing.Flat(contents).Count, "and every other entry is still returned");

        workspace.Dispose();
    }

    /// <summary>
    /// Preloading is not a second answer to the same question — it is the same reading, done during the
    /// listing, so nothing is read again afterwards.
    /// </summary>
    /// <remarks>
    /// The record is deleted between the listing and the access, which is the only way to tell "was already
    /// read" from "was read just now": a listing that read lazily would raise here, and one that answers
    /// from what it already has does not.
    /// </remarks>
    private static async Task NativePreloadMakesTheDetailsReadAlready(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var (contents, items, indexPaths) = SyntheticListing(1, detailsAlreadyRead: true);
        var item = items[0];
        Check.True(item.IsDetailsLoaded, "an entry that came pre-read has nothing pending");

        File.Delete(indexPaths[0]);

        Check.True(item.OriginalPath.Length > 0, "the path is still answerable after its record is gone");
        Check.True(item.DeletedAt > DateTimeOffset.Now.AddHours(-1), $"and so is the deletion time (got {item.DeletedAt:O})");

        workspace.Dispose();
    }

    /// <summary>
    /// The two fields come out of one record, so the first access to either answers both — in either order,
    /// and whichever was asked for.
    /// </summary>
    /// <remarks>
    /// The record is deleted between the first access and the second, which is the only way to tell "the same
    /// reading answered both" from "the second access read the record itself": a property that read on every
    /// access would raise here.
    /// <para>
    /// The two orders need two entries and two listings. One item answers the first access once and cannot be
    /// asked to be the first again.
    /// </para>
    /// </remarks>
    private static async Task NativeLazyDetailsAreBothAnsweredByTheFirstAccess(TestContext context)
    {
using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var (contents, items, indexPaths) = SyntheticListing(2, detailsAlreadyRead: false);

        // Two entries, so that each order gets a first access of its own — one item cannot be asked to be the
        // first twice.
        var pathFirst = items[0];
        var dateFirst = items[1];

        Check.True(pathFirst.OriginalPath.Length > 0, "asking for the path answers it");
        File.Delete(indexPaths[0]);
        Check.True(
            pathFirst.DeletedAt > DateTimeOffset.Now.AddHours(-1),
            $"and the time is still answerable afterwards, from the same reading (got {pathFirst.DeletedAt:O})");

        Check.True(dateFirst.DeletedAt > DateTimeOffset.Now.AddHours(-1), $"asking for the time answers it ({dateFirst.DeletedAt:O})");
        File.Delete(indexPaths[1]);
        Check.True(
            dateFirst.OriginalPath.Length > 0,
            "and the path is still answerable afterwards, from the same reading");

        workspace.Dispose();
    }

    /// <summary>
    /// An entry whose record has been collected under it is an honest failure, raised once: the caller is
    /// told rather than handed a path that was never there, and a second access does not go back to the disk
    /// to find out again.
    /// </summary>
    private static async Task NativeLazyDetailsThrowOnceWhenIndexIsGone(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an entry to lose its record under the caller");

        var (contents, items, indexPaths) = SyntheticListing(1, detailsAlreadyRead: false);
        var item = items[0];

        File.Delete(indexPaths[0]);

        var first = Check.Throws<IOException>(() => _ = item.OriginalPath, "reading a path whose record is gone raises");
        Check.True(
            first.Message.Contains("$I", StringComparison.OrdinalIgnoreCase) || first.Message.Contains("record", StringComparison.OrdinalIgnoreCase),
            $"and says what could not be read ({first.Message})");

        var second = Check.Throws<IOException>(() => _ = item.DeletedAt, "reading the time raises too");
        Check.Equal(first.Message, second.Message, "with the same answer, because the attempt is not repeated");

        workspace.Dispose();
    }

    /// <summary>
    /// A payload nothing describes is the other half of the same idea. It has a size and a kind, but no
    /// record to say what it was, so there is no entry to report — and it is not an error either.
    /// </summary>
    private static async Task NativeListingIgnoresAPayloadWithoutARecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin to leave an unpaired payload in");

        var stray = Path.Combine(BinDirectory, "$RSTRAY" + RunTag + ".bin");
        File.WriteAllBytes(stray, Content);

        try
        {
            var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);

            Check.False(
                Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).Any(i => SamePath(i.PayloadPath, stray)),
                "an entry with no record is not listed, because it has no original path to answer with");
            Check.Equal(0, contents.Failures.Count, "and it is not a failure: it is a state the bin is briefly in");

            var pairs = NativeBinEntries.Collect(BinDirectory, context.CancellationToken);
            Check.True(
                pairs.Any(e => string.Equals(e.PayloadPath, stray, StringComparison.OrdinalIgnoreCase) && e.IndexPath is null),
                "the enumeration does report it, as a payload with no record");
        }
        finally
        {
            File.Delete(stray);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// An entry whose payload is gone is still an entry, and an empty has to take it: its record is the only
    /// thing left of it, and leaving that behind is how a bin fills with records nothing describes.
    /// </summary>
    /// <remarks>
    /// Run against a bin inside the scratch root rather than the run's own volume. This case empties a bin,
    /// and emptying the run's real Recycle Bin costs seconds and changes what every later case sees on it —
    /// which is how a listing case once made an unrelated timing-sensitive case fail.
    /// <para>
    /// The orphan's size comes from its record, since there is no payload left to measure, and it is counted
    /// like any other entry: "exact counts" is a claim about every entry removed, not about the ones that
    /// still had files.
    /// </para>
    /// </remarks>
    private static async Task NativeEmptyCollectsAnEntryWhosePayloadIsGone(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var root = Path.Combine(ScratchWorkspace.Root, "orphan-volume") + Path.DirectorySeparatorChar;
        var bin = NativeBinLayout.EnsureBinDirectory(root);

        const long orphanSize = 1234;
        var deleted = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);

        var wholeStem = "AAA" + RunTag;
        var wholeIndex = NativeBinLayout.IndexPath(bin, wholeStem, ".bin");
        var wholePayload = NativeBinLayout.PayloadPath(bin, wholeStem, ".bin");

        File.WriteAllBytes(wholePayload, Content);
        NativeIndexFile.Write(wholeIndex, ScratchWorkspace.Root + "\\whole.bin", Content.LongLength, deleted);

        var orphanStem = "BBB" + RunTag;
        var orphanIndex = NativeBinLayout.IndexPath(bin, orphanStem, ".bin");
        NativeIndexFile.Write(orphanIndex, ScratchWorkspace.Root + "\\orphan.bin", orphanSize, deleted);

        var result = await Task
            .Run(() => NativeBinEmptier.Empty([root], context.CancellationToken), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(0, result.Failures.Count, $"nothing fails (got {result.Failures.Count})");
        Check.Equal(2, result.ItemsRemoved, "both entries are counted as removed, the one with no payload included");
        Check.Equal(
            Content.LongLength + orphanSize,
            result.BytesFreed,
            "and the orphan's size is read from its record, because its payload is what is gone");
        Check.True(
            result.VolumesEmptied.Count == 1 && result.VolumesEmptied[0] == root,
            $"the volume is named as emptied, because nothing was left behind (got [{string.Join(", ", result.VolumesEmptied)}])");

        Check.False(File.Exists(wholeIndex), "the whole entry's record is gone");
        Check.False(File.Exists(wholePayload), "and so is its payload");
        Check.False(File.Exists(orphanIndex), "and the orphaned record is collected, which is the whole point");

        workspace.Dispose();
    }

    /// <summary>
    /// A record is a file. A directory that happens to be named like one is not a record, and an empty that
    /// tried to delete it would fail on every run for ever and take the volume out of its results.
    /// </summary>
    /// <remarks>
    /// Windows never writes one, but the bin directory belongs to whoever runs this and is writable by them,
    /// so the state is reachable from the outside and the sweep must not trip over it.
    /// </remarks>
    private static async Task NativeEmptyIgnoresADirectoryNamedLikeARecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var root = Path.Combine(ScratchWorkspace.Root, "decoy-volume") + Path.DirectorySeparatorChar;
        var bin = NativeBinLayout.EnsureBinDirectory(root);

        var decoy = Path.Combine(bin, "$IDECOY" + RunTag + ".bin");
        Directory.CreateDirectory(decoy);

        var realStem = "CCC" + RunTag;
        var realIndex = NativeBinLayout.IndexPath(bin, realStem, ".bin");
        var realPayload = NativeBinLayout.PayloadPath(bin, realStem, ".bin");
        File.WriteAllBytes(realPayload, Content);
        NativeIndexFile.Write(realIndex, ScratchWorkspace.Root + "\\real.bin", Content.LongLength, DateTime.UtcNow);

        var entries = NativeBinEntries.Collect(bin, context.CancellationToken);
        Check.False(
            entries.Any(e => e.IndexPath is not null && string.Equals(e.IndexPath, decoy, StringComparison.OrdinalIgnoreCase)),
            "the enumeration does not take a directory for a record");

        var result = await Task
            .Run(() => NativeBinEmptier.Empty([root], context.CancellationToken), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(0, result.Failures.Count, $"the decoy costs the caller nothing (got {result.Failures.Count})");
        Check.Equal(1, result.ItemsRemoved, "and the real entry is removed as the only one there was");
        Check.True(
            result.VolumesEmptied.Count == 1 && result.VolumesEmptied[0] == root,
            $"so the volume is still named as emptied (got [{string.Join(", ", result.VolumesEmptied)}])");
        Check.True(Directory.Exists(decoy), "the decoy is left exactly where it was — it is not this empty's to remove");

        workspace.Dispose();
    }

    /// <summary>
    /// A record that opens and turns out not to be one is a different thing from a record that could not be
    /// opened, and the exception says which — because "removed or locked" is a diagnosis a caller would act
    /// on, and acting on it wrongly sends them looking for a race that never happened.
    /// </summary>
    private static async Task NativeLazyDetailsSayWhatHappenedWhenTheRecordIsNotARecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin to leave a malformed record in");

        var indexPath = Path.Combine(BinDirectory, "$IBROKEN" + RunTag + ".bin");
        var payloadPath = NativeBinLayout.ToPayloadPath(indexPath);

        File.WriteAllBytes(indexPath, [1, 2, 3]);
        File.WriteAllBytes(payloadPath, Content);

        try
        {
            var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken).ConfigureAwait(false);
            var item = Listing.OnlyEntries(contents, ScratchWorkspace.VolumeRoot).First(i => SamePath(i.PayloadPath, payloadPath));

            var raised = Check.Throws<IOException>(() => _ = item.OriginalPath, "a file that is not a record has no path to answer with");

            Check.False(
                raised.Message.Contains("removed or locked", StringComparison.OrdinalIgnoreCase),
                $"and the message does not blame a removal that did not happen ({raised.Message})");
            Check.True(
                raised.Message.Contains("record", StringComparison.OrdinalIgnoreCase),
                $"but says what is actually wrong — that the file is not a record ({raised.Message})");
        }
        finally
        {
            File.Delete(indexPath);
            File.Delete(payloadPath);
        }

        workspace.Dispose();
    }

    /// <summary>
    /// The whole API reports failures with the code the operating system gave. An exception raised from a lazy
    /// property is a failure too, and a caller that reads <c>HResult</c> here expects the same thing.
    /// </summary>
    private static async Task NativeLazyDetailsCarryTheFileSystemCode(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an entry to lose its record under the caller");

        var (contents, items, indexPaths) = SyntheticListing(1, detailsAlreadyRead: false);
        var item = items[0];

        File.Delete(indexPaths[0]);

        var raised = Check.Throws<IOException>(() => _ = item.OriginalPath, "reading a path whose record is gone raises");

        // 0x80131620 is COR_E_EXCEPTION, what a plain `new IOException(message)` carries. A caller told to look
        // at the code needs the filesystem's own, not the runtime's default for an exception with no code.
        Check.NotEqual(
            unchecked((int)0x80131620),
            raised.HResult,
            $"the filesystem's own code is carried, not the runtime default (got 0x{raised.HResult:X8})");

        workspace.Dispose();
    }

    /// <summary>
    /// The two halves of an entry are told apart by one letter, and Windows does not promise to write it the
    /// same way twice. A pair that failed to match because of a capital would be two entries that each lack
    /// what the other carries: one nothing can describe, one nothing can restore.
    /// </summary>
    /// <remarks>
    /// Written by hand into the bin rather than recycled, because both halves are written in one
    /// case and another shape is needed in the other; neither can be asked to produce a mismatch
    /// on demand. The tails are what the pairing keys on, so they are spelled differently too.
    /// </remarks>
    private static async Task NativeEnumerationPairsHalvesWhateverCaseTheyAreWrittenIn(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        // A bin of the case's own inside the scratch root: the entries are written by hand rather than
        // recycled, because neither shape can be asked to disagree about the case of the marker letter
        // on demand.
        var root = Path.Combine(ScratchWorkspace.Root, "case-volume") + Path.DirectorySeparatorChar;
        var bin = NativeBinLayout.EnsureBinDirectory(root);

        var lowercaseRecord = Path.Combine(bin, "$ilower" + RunTag + ".bin");
        var lowercasePayload = Path.Combine(bin, "$rlower" + RunTag + ".bin");
        var upperRecord = Path.Combine(bin, "$IUPPER" + RunTag + ".BIN");
        var upperPayload = Path.Combine(bin, "$RUPPER" + RunTag + ".BIN");

        File.WriteAllBytes(lowercasePayload, Content);
        File.WriteAllBytes(upperPayload, Content);
        NativeIndexFile.Write(lowercaseRecord, root + "lower-" + RunTag + ".bin", Content.LongLength, DateTime.UtcNow);
        NativeIndexFile.Write(upperRecord, root + "upper-" + RunTag + ".bin", Content.LongLength, DateTime.UtcNow);

        var entries = NativeBinEntries.Collect(bin, context.CancellationToken);

        var lower = entries.SingleOrDefault(e => SamePath(e.IndexPath ?? string.Empty, lowercaseRecord));
        Check.NotNull(lower, "a lowercase $i pairs with a lowercase $r");
        Check.True(
            SamePath(lower!.PayloadPath ?? string.Empty, lowercasePayload),
            $"and both halves end up on one entry (got '{lower.PayloadPath}')");

        var upper = entries.SingleOrDefault(e => SamePath(e.IndexPath ?? string.Empty, upperRecord));
        Check.NotNull(upper, "an uppercase $I pairs with an uppercase $R whatever the extension's case is");
        Check.True(
            SamePath(upper!.PayloadPath ?? string.Empty, upperPayload),
            $"and both halves end up on one entry (got '{upper.PayloadPath}')");

        Check.Equal(
            2,
            entries.Count(e => (SamePath(e.IndexPath ?? string.Empty, lowercaseRecord) || SamePath(e.IndexPath ?? string.Empty, upperRecord)) && e.PayloadPath is not null),
            "and neither pair was reported as two half-entries");

        workspace.Dispose();
    }

/// <summary>
    /// A recycled directory is reported as zero bytes, and the enumeration that builds the item says so before
    /// any listing exists.
    /// </summary>
    /// <remarks>
    /// This pins <see cref="NativeBinEntry"/>'s own contract rather than repeating what
    /// <c>NativeListingReportsARecycledDirectoryAsADirectory</c> already asserts about the listing. It cannot
    /// show that the <c>IsDirectory</c> guard in the walk is load-bearing either: NTFS
    /// <c>FindFirstFile</c> reports zero for a directory regardless, so removing that guard would leave this
    /// green. The guard stays as defensiveness, and this is here because the figure is part of what the type
    /// promises.
    /// </remarks>
    private static async Task NativeListingReportsAZeroSizeForARecycledDirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for a directory to be listed out of");

        var directory = Path.Combine(ScratchWorkspace.Root, "zero-sized-" + RunTag);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "inside.bin"), Content);

        var recycled = await RecycleBin.RecycleAsync([directory], RecycleOptions.Default, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the directory is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var directoryIndex = RecordOf(directory);
        var entries = NativeBinEntries.Collect(BinDirectory, context.CancellationToken);
        var enumerated = entries.SingleOrDefault(e => SamePath(e.IndexPath ?? string.Empty, directoryIndex));

        Check.NotNull(enumerated, $"the enumeration pairs the directory's record ({entries.Count} pair(s) in the bin)");
        Check.True(enumerated!.PayloadIsDirectory, "the enumeration reports the payload as a directory");
        Check.Equal(0, enumerated.PayloadLength, "with no size of its own for the directory record to carry");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, GetItemsOptions.Default, context.CancellationToken), directory);
        Check.Equal(0, item.Size, "and the listing reports the same zero size");

        workspace.Dispose();
    }

    /// <summary>
    /// The point of the method: after it, nothing is left to read. Taking every record away underneath the
    /// listing is how that is shown — an item still answers its path and its time, which it could not do if
    /// the record were the source it had not already read.
    /// </summary>
    private static async Task LoadItemDetailsReadsEveryPendingRecord(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for entries to load details from");

        var (contents, items, indexPaths) = SyntheticListing(3, detailsAlreadyRead: false);

        contents.LoadItemDetails(context.CancellationToken);

        foreach (var indexPath in indexPaths)
        {
            File.Delete(indexPath);
        }

        foreach (var item in items)
        {
            Check.True(item.OriginalPath.Length > 0, "a path survives the record it came from being taken away");
            Check.True(item.DeletedAt > DateTimeOffset.Now.AddHours(-1), $"and so does its time ({item.DeletedAt:O})");
        }

        workspace.Dispose();
    }

    /// <summary>
    /// Work already done is not done twice. A listing that read its records up front has nothing left to
    /// load, and a method that went looking anyway would pay for every record on a bin that already cost a
    /// read each — twice over.
    /// </summary>
    private static async Task LoadItemDetailsSkipsWhatTheListingAlreadyRead(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an entry the listing already read");

        // Already read, which is what a listing taken with PreloadItemDetails leaves behind — built here
        // rather than by asking the backend for one, because this case then has to take a record away and it
        // must be one of its own.
        var (contents, items, indexPaths) = SyntheticListing(1, detailsAlreadyRead: true);

        Check.True(items[0].IsDetailsLoaded, "an entry that came pre-read has nothing pending");

        File.Delete(indexPaths[0]);

        contents.LoadItemDetails(context.CancellationToken);

        Check.True(items[0].OriginalPath.Length > 0, "an already-loaded entry is skipped, and answers from what it had");
        Check.True(items[0].DeletedAt > DateTimeOffset.Now.AddHours(-1), $"with its time too ({items[0].DeletedAt:O})");

        workspace.Dispose();
    }

    /// <summary>
    /// An entry whose record cannot be read is left exactly as the listing left it: cold, and raising the
    /// same exception at the moment a caller asks. The method does not throw, does not half-load it, and does
    /// not turn one dead entry into a failed batch.
    /// </summary>
    private static async Task LoadItemDetailsLeavesAnUnreadableRecordCold(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an entry to lose its record before the load");

        var (contents, items, indexPaths) = SyntheticListing(1, detailsAlreadyRead: false);

        File.Delete(indexPaths[0]);

        contents.LoadItemDetails(context.CancellationToken);

        Check.Throws<IOException>(
            () => _ = items[0].OriginalPath,
            "the entry is still cold, so the caller still hears about it at the point of asking");

        workspace.Dispose();
    }


    /// <summary>
    /// The record of a listed entry, worked out from the listing rather than found by reading every record in
    /// the bin looking for the one that names a path.
    /// </summary>
    /// <remarks>
    /// The old shape of this — enumerate <c>$I*</c>, read each, compare what it says — costs one file read per
    /// record in the user's Recycle Bin, which on a mechanical volume with tens of thousands of entries is the
    /// half-minute this suite exists to avoid. A listing already holds the pairing.
    /// </remarks>
    private static string RecordOf(RecycleBinItem item) => NativeBinLayout.ToIndexPath(item.PayloadPath);

    /// <summary>
    /// The volume a batch groups by comes out of the entry's own record path.
    /// </summary>
    /// <remarks>
    /// The share case is here because it is the one root whose spelling a caller might expect to match
    /// <c>NormalizeVolumeRoot</c> and does not: a path's root has no trailing separator, so
    /// <c>\\server\share</c> is not the key <c>\\server\share\</c> would use. It answers the same either way —
    /// one thread, a share being <see cref="VolumeStorageKind.Unknown"/> — but a reader who changes this should
    /// know that the two miss each other in the cache.
    /// </remarks>
    private static Task VolumeRootIsTheRecordsOwn(TestContext context)
    {
        var local = new RecycleBinItem(@"X:\$RECYCLE.BIN\SID\$IABC123.bin", @"X:\$RECYCLE.BIN\SID\$RABC123.bin", 10, false);
        var share = new RecycleBinItem(@"\\server\share\$RECYCLE.BIN\SID\$IABC123.bin", @"\\server\share\$RECYCLE.BIN\SID\$RABC123.bin", 10, false);

        Check.Equal(@"X:\", local.VolumeRoot.ToString(), "a local entry's volume is the root of its record");
        Check.Equal(@"\\server\share", share.VolumeRoot.ToString(), "a share's volume is the share, without a trailing separator");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Warming an entry publishes two values at once, and a caller reading them from another thread must never
    /// see one of each.
    /// </summary>
    /// <remarks>
    /// Run against one entry shared by every thread rather than one per thread: the fields the threads race for
    /// are the pair inside the item, and giving each its own would test nothing.
    /// <para>
    /// What this is, precisely, is a stress check and not a proof of the ordering: inverting the two statements
    /// in <c>LoadDetails</c> — publishing before filling, rather than filling before publishing — leaves this
    /// green, because the window between them is a handful of instructions wide. The ordering rests on the
    /// release/acquire pairing documented there and on the fact that the entry's two values only ever change
    /// once. What this does catch is the coarse failures: an entry whose pair is never written, or one whose
    /// fields are read from two different places.
    /// </para>
    /// </remarks>
    private static Task LoadDetailsStaysConsistentWhenManyThreadsRaceIt(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var (contents, items, indexPaths) = SyntheticListing(1, detailsAlreadyRead: false);
        var item = items[0];
        var indexPath = indexPaths[0];

        Check.True(NativeIndexFile.TryRead(indexPath, out var record), "the record is readable to compare against");
        var expectedPath = record.OriginalPath;
        var expectedTime = new DateTimeOffset(record.DeletedAtUtc.ToLocalTime());

        const int Threads = 10;
        const int Rounds = 200;

        using var stop = new CancellationTokenSource();

        var workers = new Thread[Threads];
        for (var t = 0; t < Threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (var round = 0; round < Rounds && !stop.IsCancellationRequested; round++)
                {
                    item.LoadDetails(out _);
                    _ = item.OriginalPath;
                    _ = item.DeletedAt;
                }
            });
            workers[t].Start();
        }

        foreach (var worker in workers)
        {
            if (!worker.Join(TimeSpan.FromSeconds(30)))
            {
                stop.Cancel();
                throw new CheckFailedException("a thread warming the same entry did not finish; the entry's fields disagree with themselves");
            }
        }

        stop.Cancel();

        Check.Equal(expectedPath, item.OriginalPath, "the path is the record's own after ten threads warmed the entry");
        Check.Equal(expectedTime, item.DeletedAt, "and the time is the same record's, not another entry's");

        workspace.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// A listing over entries this case made inside the scratch root, and the bin they live in.
    /// </summary>
    /// <remarks>
    /// Built by hand rather than by listing a volume, because a case that has to take a record away has to take
    /// away one of its own. A real bin may hold entries from other cases and from previous runs; deleting the
    /// record of any of those leaves its payload in the user's Recycle Bin with nothing that will ever collect
    /// it, and a run repeated often enough fills their bin with orphans of files they once recycled.
    /// <para>
    /// The whole bin lives under <see cref="ScratchWorkspace.Root"/>, which the run removes at the end, so a
    /// case can leave litter behind without leaving it behind.
    /// </para>
    /// </remarks>
    private static (RecycleBinContents Contents, RecycleBinItem[] Items, IReadOnlyList<string> IndexPaths) SyntheticListing(
        int count,
        bool detailsAlreadyRead)
    {
        var root = Path.Combine(ScratchWorkspace.Root, "synthetic-volume") + Path.DirectorySeparatorChar;
        var bin = NativeBinLayout.EnsureBinDirectory(root);

        var deleted = DateTime.UtcNow;
        var items = new RecycleBinItem[count];
        var indexPaths = new string[count];

        for (var i = 0; i < count; i++)
        {
            var stem = $"SYN{i}D" + RunTag;
            var indexPath = NativeBinLayout.IndexPath(bin, stem, ".bin");
            var payloadPath = NativeBinLayout.PayloadPath(bin, stem, ".bin");
            var originalPath = $"{root}synthetic-{i}-{RunTag}.bin";

            File.WriteAllBytes(payloadPath, Content);
            NativeIndexFile.Write(indexPath, originalPath, Content.LongLength, deleted);

            indexPaths[i] = indexPath;
            items[i] = detailsAlreadyRead
                ? new RecycleBinItem(indexPath, payloadPath, Content.LongLength, false, originalPath, new DateTimeOffset(deleted.ToLocalTime()))
                : new RecycleBinItem(indexPath, payloadPath, Content.LongLength, false);
        }

        return (new RecycleBinContents([new RecycleBinVolumeContents(root, items)], [], TimeSpan.Zero), items, indexPaths);
    }

    /// <summary>
    /// Whether a listed entry was deleted from <paramref name="path"/>, without regard to case.
    /// </summary>
    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The record of the newest entry in the current user's bin that names <paramref name="originalPath"/>.
    /// Listing before and after is what tells the two apart: the bin may already hold anything at all, and
    /// a case that assumed it was empty would be asserting against whatever happened to be there.
    /// </summary>
    private static NativeIndexRecord ReadNewestRecord(long expectedSize, string originalPath)
    {
        var indexPath = RecordOf(originalPath);

        Check.True(NativeIndexFile.TryRead(indexPath, out var record), $"and it is readable ({indexPath})");
        Check.Equal(expectedSize, record.Size, $"the record of {originalPath} carries the size {expectedSize}");

        return record;
    }

    /// <summary>
    /// The index record the operation for <paramref name="originalPath"/> wrote, found by listing the bin
    /// and matching on what each record says. Returns <see langword="null"/> when the operation wrote none.
    /// </summary>
    private static string RecordOf(string originalPath) =>
        NewestIndexMatching(originalPath)
        ?? throw new CheckFailedException($"no index record in {BinDirectory} names {originalPath}");

    /// <summary>
    /// The newest index record whose recorded original path is <paramref name="originalPath"/>. Reading the
    /// record is the only way to match, because the entry's own name is random and tells nobody what it was.
    /// </summary>
    private static string? NewestIndexMatching(string originalPath)
    {
        var matching = new List<string>();

        foreach (var path in IndexRecords())
        {
            if (NativeIndexFile.TryRead(path, out var record) &&
                string.Equals(record.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            {
                matching.Add(path);
            }
        }

        return matching.Count == 0 ? null : matching.MaxBy(File.GetLastWriteTimeUtc);
    }

    /// <summary>Every index record in the current user's bin on the run's volume.</summary>
    private static IReadOnlyList<string> IndexRecords() =>
        Directory.Exists(BinDirectory)
            ? [.. Directory.EnumerateFiles(BinDirectory, "$I*", SearchOption.TopDirectoryOnly)]
            : [];

    private static string BinDirectory =>
        Path.Combine(ScratchWorkspace.VolumeRoot, "$RECYCLE.BIN", NativeBinLayout.CurrentSid);

    /// <summary>
    /// The helper directories in the volume root. Counted rather than
    /// listed, so a case says how many there were and not what they were called.
    /// </summary>
    private static int CountHelperDirectories()
    {
        try
        {
            return Directory.EnumerateDirectories(ScratchWorkspace.VolumeRoot, "$RCW-*", SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Makes every file this suite creates unique to this process, so that two runs on one machine cannot
    /// put the same original path in the Recycle Bin twice.
    /// </summary>
    /// <remarks>
    /// The Recycle Bin is the one piece of state here that outlives a run: a leftover from the previous one
    /// is still there, and a case that asserts "exactly one listed entry names this path" would see two the
    /// second time it was run against the same volume. The path is the only thing a recycled record keeps,
    /// so it has to differ between runs for the assertion to be about this run's operation.
    /// </remarks>
    private static readonly string RunTag = $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks % 1_000_000}";

    private static string CreateFile(string name)
    {
        var path = Path.Combine(ScratchWorkspace.Root, name + "-" + RunTag + ".bin");
        File.WriteAllBytes(path, Content);
        return path;
    }

    /// <summary>Length of the file name <see cref="CreateDeepFile"/> ends with, including its separator.</summary>
    private const int DeepFileNameLength = 105;

    /// <summary>
    /// A file whose whole path is at least <paramref name="minimumLength"/> characters. Built as a chain of
    /// directories because one component cannot exceed 255 characters, which is also the shape a recursive
    /// delete of a mixed tree has to cope with.
    /// </summary>
    private static string CreateDeepFile(int minimumLength)
    {
        var directory = ScratchWorkspace.Root;
        var component = new string('d', 118);

        while (directory.Length + DeepFileNameLength < minimumLength)
        {
            var deeper = Path.Combine(directory, component + directory.Length);
            Directory.CreateDirectory(deeper);
            directory = deeper;
        }

        var path = Path.Combine(directory, new string('f', 100) + "-" + RunTag + ".bin");
        File.WriteAllBytes(path, Content);
        return path;
    }

    private static byte[] CreateContent()
    {
        var content = new byte[4096];
        new Random(20261010).NextBytes(content);
        return content;
    }

    private static string Describe(RecycleFailure? failure) =>
        failure is null ? "none" : $"{failure.Stage} 0x{failure.HResult:X8} {failure.Path}";
}
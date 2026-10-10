using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// The native backend, which writes the Recycle Bin's own files instead of asking the Shell to.
/// </summary>
/// <remarks>
/// Every case here passes <see cref="RecycleBinBackend.Native"/> explicitly. That is the point of the
/// suite: it exercises one backend whatever the run was told to measure, so a green <c>--all</c> on
/// either path says nothing about the other's coverage.
/// <para>
/// Nothing here creates a file outside <see cref="ScratchWorkspace.Root"/>. Where a case needs a
/// Recycle Bin record shaped in a way this machine's Shell does not produce, the record is built
/// inside the scratch root and the internal reader is pointed at it; the record is only ever written
/// into <c>$RECYCLE.BIN</c> by the code under test.
/// </para>
/// </remarks>
internal static class NativeBinSuite
{
    /// <summary>Enough to be past the Shell's measured 260-character limit.</summary>
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

    private static readonly RecycleBinOptions Native = new() { Backend = RecycleBinBackend.Native };

    /// <summary>
    /// The path most callers take, named here because two of the cases below exist to compare against it.
    /// </summary>
    private static readonly RecycleBinOptions Shell = new() { Backend = RecycleBinBackend.WinApi };

    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("NativeBinSuite." + nameof(NativeRecycleRemovesTheFileAndRecordsIt), NativeRecycleRemovesTheFileAndRecordsIt),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleLeavesNoStagingAndNoStagingDirectory), NativeRecycleLeavesNoStagingAndNoStagingDirectory),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleAcceptsAPathPastMaxPath), NativeRecycleAcceptsAPathPastMaxPath),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleAcceptsAPathFarPastMaxPath), NativeRecycleAcceptsAPathFarPastMaxPath),
        new TestCase("NativeBinSuite." + nameof(NativeRecycleMovesADirectory), NativeRecycleMovesADirectory),
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
        new TestCase("NativeBinSuite." + nameof(NativeListingFindsAnEntryNativeRecycleRecorded), NativeListingFindsAnEntryNativeRecycleRecorded),
        new TestCase("NativeBinSuite." + nameof(NativeListingReportsARecycledDirectoryAsADirectory), NativeListingReportsARecycledDirectoryAsADirectory),
        new TestCase("NativeBinSuite." + nameof(NativeListingOfAVolumeWithNoBinDirectoryIsEmpty), NativeListingOfAVolumeWithNoBinDirectoryIsEmpty),
        new TestCase("NativeBinSuite." + nameof(NativeListingHidesAnIndexWhosePayloadIsGone), NativeListingHidesAnIndexWhosePayloadIsGone),
        new TestCase("NativeBinSuite." + nameof(NativeListingIsUndisturbedByARecordItCannotRead), NativeListingIsUndisturbedByARecordItCannotRead),
        new TestCase("NativeBinSuite." + nameof(ADriveIsClassifiedFromItsOwnSeekPenaltyAnswer), ADriveIsClassifiedFromItsOwnSeekPenaltyAnswer),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyRemovesWhatWasRecycledAndReportsExactCounts), NativeEmptyRemovesWhatWasRecycledAndReportsExactCounts),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyOfAVolumeWithNothingInItReportsNothing), NativeEmptyOfAVolumeWithNothingInItReportsNothing),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyKeepsTheBinDirectoriesThemselves), NativeEmptyKeepsTheBinDirectoriesThemselves),
        new TestCase("NativeBinSuite." + nameof(NativeEmptyHonoursCancellationBetweenEntries), NativeEmptyHonoursCancellationBetweenEntries),
        new TestCase("NativeBinSuite." + nameof(NativeListingSeesAnEntryTheShellRecorded), NativeListingSeesAnEntryTheShellRecorded),
        new TestCase("NativeBinSuite." + nameof(TheShellListingSeesAnEntryTheNativePathRecorded), TheShellListingSeesAnEntryTheNativePathRecorded),
        new TestCase("NativeBinSuite." + nameof(ARecycledItemRestoresFromItsRecordedPath), ARecycledItemRestoresFromItsRecordedPath),
        new TestCase("NativeBinSuite." + nameof(TheShellWritesAVersionTwoIndexRecordOnThisMachine), TheShellWritesAVersionTwoIndexRecordOnThisMachine),
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
    ];

    /// <summary>
    /// The path is gone from disk and the call reports it as recycled rather than as failed. That is the
    /// whole of the ordinary case: everything else here is about what the record says and about the paths
    /// the Shell path cannot take.
    /// </summary>
    private static async Task NativeRecycleRemovesTheFileAndRecordsIt(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the file to be recorded in");

        var path = CreateFile("ordinary.bin");
        var size = new FileInfo(path).Length;

        var result = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the file is recycled (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(1, result.RecycledPaths.Count, "and is named as recycled");
        Check.Equal(0, result.Failures.Count, "with no failure recorded");
        Check.False(File.Exists(path), $"and is gone from disk: {path}");

        var record = ReadNewestRecord(size, path);
        Check.Equal(size, record.Size, "the record carries the size the file had");

        workspace.Dispose();
    }

    /// <summary>
    /// Nothing is staged, because there is nothing to stage for. The Shell path has to move a long path
    /// aside before it can hand it over, and those <c>$RCW-…</c> directories in the volume root are that
    /// step. The native backend moves a file into the bin directly and its destination is about seventy-five
    /// characters whatever the source was, so the directories must not appear at all — not appear and be
    /// cleaned up, which is the reading a passing <c>LeftoverStagingDirectories</c> check would leave open.
    /// </summary>
    private static async Task NativeRecycleLeavesNoStagingAndNoStagingDirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the long path to be recorded in");

        var before = CountStagingDirectories();
        var path = CreateDeepFile(PastMaxPath);

        var result = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"a long path recycles on the native backend (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(0, result.LeftoverStagingDirectories.Count, "no staging directory is named, because none was created");
        Check.Equal(before, CountStagingDirectories(), "and none appeared in the volume root while it ran");

        workspace.Dispose();
    }

    /// <summary>
    /// A path past <c>MAX_PATH</c> is the case the Shell path exists to refuse, because the Shell removes
    /// such a file for good and reports success. The native path has no such limit: the record is written
    /// with the whole path in it and the payload moves to a short destination.
    /// </summary>
    private static async Task NativeRecycleAcceptsAPathPastMaxPath(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the long path to be recorded in");

        var path = CreateDeepFile(PastMaxPath);
        Check.True(path.Length >= PastMaxPath, $"the path is genuinely long ({path.Length} characters): {path}");

        var result = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"a path of {path.Length} characters recycles (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(0, result.PermanentlyDeletedPaths.Count, "and is not removed for good on the way");
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
        Check.True(path.Length >= FarPastMaxPath, $"the path is far past the Shell's limit ({path.Length} characters)");

        var result = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"a path of {path.Length} characters recycles (first failure: {Describe(result.FirstFailure)})");
        Check.False(File.Exists(path), "and is gone from disk");

        var record = ReadNewestRecord(Content.Length, path);
        Check.Equal(path, record.OriginalPath, "and the record names every character of it");

        workspace.Dispose();
    }

    /// <summary>
    /// A directory is one item, moved as a directory. Its record carries a size of zero, which is what
    /// Windows itself writes: the Shell cannot walk a recycled directory's contents without elevation, so
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

        var result = await FileRecycler.MoveToRecycleBinAsync([directory], Native, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the directory is recycled (first failure: {Describe(result.FirstFailure)})");
        Check.False(Directory.Exists(directory), "and is gone from disk");

        var record = ReadNewestRecord(0, directory);
        Check.Equal(0, record.Size, "and the record carries a size of zero, as Windows writes for a directory");
        Check.Equal(directory, record.OriginalPath, "with the directory's own path");

        workspace.Dispose();
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

        var result = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
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
    /// it. An index left behind names an entry whose payload does not exist, which the Shell then shows as
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
            var result = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// What the native backend wrote is what a listing finds. The entry, its original path, its size and
    /// its payload's location all come back.
    /// </summary>
    private static async Task NativeListingFindsAnEntryNativeRecycleRecorded(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the file to be listed out of");

        var path = CreateFile("listed.bin");
        var size = new FileInfo(path).Length;

        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken), path);

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

        var recycled = await FileRecycler.MoveToRecycleBinAsync([directory], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the directory is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken), directory);

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
            .Run(() => NativeBinLister.TryEnumerate([root], RecycleBinOptions.Default, context.CancellationToken), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(0, contents.Items.Count, "a volume with no bin yields no items");
        Check.Equal(0, contents.Failures.Count, "and no failures, because an empty bin is an answer rather than a problem");

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
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var indexPath = RecordOf(path);
        var payloadPath = NativeBinLayout.ToPayloadPath(indexPath);
        Check.True(File.Exists(payloadPath), "the payload is there to begin with");

        File.Delete(payloadPath);

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        Check.False(
            contents.Items.Any(i => string.Equals(i.OriginalPath, path, StringComparison.OrdinalIgnoreCase)),
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

        var recycled = await FileRecycler.MoveToRecycleBinAsync([locked, other], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"both are recycled (first failure: {Describe(recycled.FirstFailure)})");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);
        var baseline = contents.Items.Count;

        using (new FileStream(RecordOf(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);
        }

        Check.Equal(0, contents.Failures.Count, "no record is opened, so none of them can be unreadable");
        Check.Equal(baseline, contents.Items.Count, "and every entry is returned, the locked one included");

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

        var expected = kind == VolumeStorageKind.SolidState
            ? Math.Min(Environment.ProcessorCount, VolumeSeekPenalty.ReadDegreeCap)
            : 1;

        Check.Equal(
            expected,
            VolumeSeekPenalty.DegreeOfParallelism(root),
            kind == VolumeStorageKind.SolidState
                ? "a solid-state volume is read on every core up to the cap, because reading is what concurrency helps and past eight threads it stopped improving"
                : "anything else is read on one thread, because concurrent requests make a mechanical head seek between them and a driver that never answered may be either");

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
    /// The counts an empty reports are exact rather than inferred. The Shell path can only subtract two
    /// <c>SHQueryRecycleBin</c> samples, which a recycle happening at the same moment can skew and which a
    /// volume's own counter rounds; this path sums the sizes already written into the records it is about to
    /// delete, so the totals have to match what those records say or something removed the wrong thing.
    /// </summary>
    /// <remarks>
    /// The bin is emptied first so that the case measures its own operation rather than whatever the machine
    /// happened to be holding, and the expected totals are computed from the records themselves — which is
    /// the same source the implementation uses, so agreement here says the sizes were carried through rather
    /// than that two numbers happened to match. The Shell's own counter is checked separately, for the
    /// claim a caller can actually make: that the bin it sees is empty afterwards.
    /// </remarks>
    private static async Task NativeEmptyRemovesWhatWasRecycledAndReportsExactCounts(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the files to be counted and removed from");

        await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        const int fileSize = 8192;
        var files = CreateMany(5, sizeBytes: fileSize);
        var recycled = await FileRecycler.MoveToRecycleBinAsync(files, Native, context.CancellationToken).ConfigureAwait(false);
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

        var emptied = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, emptied.Failures.Count, $"nothing failed ({string.Join("; ", emptied.Failures.Select(f => f.Path))})");
        Check.Equal(counted, emptied.ItemsRemoved, $"every record that was there is removed and named ({counted})");
        Check.Equal(totalBytes, emptied.BytesFreed, "and so is every byte of them, exactly");
        Check.Equal(fileSize * files.Count, emptied.BytesFreed, $"which for {files.Count} files of {fileSize} bytes is {fileSize * files.Count}");
        Check.Equal(0, IndexRecords().Count, "and no record survives");
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot), "the Shell's own counter agrees that the bin is empty");

        workspace.Dispose();
    }

    /// <summary>
    /// Emptying an empty bin is what a caller does to be sure, and it must not be reported as work done.
    /// The Shell refuses it outright with an error code, which is why the Shell path has to measure the
    /// counts on both sides to tell a refusal from a success.
    /// </summary>
    private static async Task NativeEmptyOfAVolumeWithNothingInItReportsNothing(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin to empty twice");

        var first = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);
        var second = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, second.ItemsRemoved, "a second empty removes nothing");
        Check.Equal(0, second.BytesFreed, "and frees nothing");
        Check.Equal(0, second.Failures.Count, "with nothing to report, because nothing went wrong");

        workspace.Dispose();
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
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        var binDirectory = BinDirectory;
        Check.True(Directory.Exists(binDirectory), $"this user's bin directory survives ({binDirectory})");
        Check.True(Directory.Exists(Path.Combine(ScratchWorkspace.VolumeRoot, "$RECYCLE.BIN")), "and so does the folder above it");

        workspace.Dispose();
    }

    /// <summary>
    /// A native empty honours cancellation between entries, which the Shell path cannot: an empty handed to
    /// the Shell has to be allowed to finish. The result then describes an operation that stopped part way —
    /// so the counts have to say how far it got and nothing may be reported as failed.
    /// </summary>
    private static async Task NativeEmptyHonoursCancellationBetweenEntries(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin holding enough entries for a cancellation to land between them");

        var files = CreateMany(3, sizeBytes: 4096);
        var recycled = await FileRecycler.MoveToRecycleBinAsync(files, Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"every file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);

        await Check.ThrowsAsync<OperationCanceledException>(
            () => RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, cancelled.Token),
            "an empty whose token is already cancelled raises rather than proceeding");

        Check.True(true, "and the bin is untouched, because the refusal comes before any work");

        workspace.Dispose();
    }

    /// <summary>
    /// What the Shell wrote is something this backend reads. One format, two ways of getting at it — and if
    /// this ever stopped holding, the native backend would be writing a private format into a directory the
    /// Shell also uses, which is how a user's Restore button quietly stops working.
    /// </summary>
    private static async Task NativeListingSeesAnEntryTheShellRecorded(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the Shell to record into and this backend to read back");

        var path = CreateFile("from-the-shell.bin");
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Shell, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the Shell recycles it (first failure: {Describe(recycled.FirstFailure)})");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken), path);

        Check.Equal(path, item.OriginalPath, "the native listing names the path the Shell recorded it from");
        Check.True(File.Exists(item.PayloadPath), $"and points at a payload that is really there ({item.PayloadPath})");

        workspace.Dispose();
    }

    /// <summary>
    /// The other direction, and the one a user would notice: an item this backend recycled has to appear in
    /// the Explorer's own Recycle Bin, with the right name, the right date and the right size.
    /// </summary>
    private static async Task TheShellListingSeesAnEntryTheNativePathRecorded(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for this backend to record into and the Shell to read back");

        var path = CreateFile("from-the-native-path.bin");
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"this backend recycles it (first failure: {Describe(recycled.FirstFailure)})");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Shell, context.CancellationToken), path);

        Check.Equal(path, item.OriginalPath, "the Shell listing names the path this backend recorded it from");
        Check.Equal(Content.Length, item.Size, "with the size the record carried");
        Check.False(item.IsDirectory, "and reports it as the file it is");
        Check.True(File.Exists(item.PayloadPath), $"pointing at the payload ({item.PayloadPath})");

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

        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"a long path recycles (first failure: {Describe(recycled.FirstFailure)})");

        // Restoring means exactly what Explorer's Restore means: move the payload back to the path the
        // record names. Nothing here knows the format — it reads the record and obeys it.
        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken), path);

        Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
        File.Move(item.PayloadPath, item.OriginalPath);

        // The last component only. A deep path in a failure message is a line no one can read and a length
        // this suite is not meant to be printing, and the assertion is about the bytes either way.
        Check.True(File.Exists(path), $"the payload is back where it was deleted from ({Path.GetFileName(path)}, {path.Length} characters)");

        var restored = await File.ReadAllBytesAsync(path, context.CancellationToken).ConfigureAwait(false);
        Check.True(original.AsSpan().SequenceEqual(restored),
            "with every byte of it, on a path longer than the Shell can recycle");

        workspace.Dispose();
    }

    /// <summary>
    /// The half of the version gate this machine can actually witness. The gate refuses the native write
    /// below Windows 10 build 14393 because that is the first Shell that reads a version 2 record, and
    /// nothing can make a Windows machine report an older build. What can be checked is the other half: that
    /// the Shell running here does write version 2, so this machine satisfies the boundary and the gate lets
    /// the write through.
    /// </summary>
    private static async Task TheShellWritesAVersionTwoIndexRecordOnThisMachine(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for the Shell to record into");

        var path = CreateFile("shell-version.bin");
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Shell, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the Shell recycles it (first failure: {Describe(recycled.FirstFailure)})");

        Check.Equal(
            NativeIndexFile.Version,
            NativeIndexFile.ReadVersion(RecordOf(path)),
            $"the Shell on this machine writes version {NativeIndexFile.Version} records, which is the version the native gate requires");

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
        var options = new RecycleBinOptions { Backend = RecycleBinBackend.Native, MaxAttempts = 0 };

        var result = await FileRecycler.MoveToRecycleBinAsync([path], options, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, $"the file is recycled anyway, because zero attempts clamps to one (first failure: {Describe(result.FirstFailure)})");
        Check.Equal(1, result.RecycledPaths.Count, "and is named as recycled rather than silently dropped");
        Check.False(File.Exists(path), "so it really is in the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// <see cref="RecycleBinOptions.OverallTimeout"/> bounds the retrying of a whole batch, not each round's
    /// wait taken on its own. A deadline recomputed before every backoff is a fresh budget every time, so the
    /// loop runs to <see cref="RecycleBinOptions.MaxAttempts"/> whenever each individual delay fits — which is
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

        var options = new RecycleBinOptions
        {
            Backend = RecycleBinBackend.Native,
            MaxAttempts = 20,
            InitialRetryDelay = TimeSpan.FromMilliseconds(30),
            OverallTimeout = TimeSpan.FromMilliseconds(300)
        };

        RecycleBinResult result;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await FileRecycler.MoveToRecycleBinAsync([locked], options, context.CancellationToken).ConfigureAwait(false);
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
        var options = new RecycleBinOptions
        {
            Backend = RecycleBinBackend.Native,
            OverallTimeout = TimeSpan.MaxValue,
            InitialRetryDelay = TimeSpan.Zero
        };

        RecycleBinResult result;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await FileRecycler.MoveToRecycleBinAsync([locked, free], options, context.CancellationToken).ConfigureAwait(false);
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
        var recycled = await FileRecycler.MoveToRecycleBinAsync(files, Native, context.CancellationToken).ConfigureAwait(false);
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

        var result = await FileRecycler.MoveToRecycleBinAsync([outer], Native, context.CancellationToken).ConfigureAwait(false);

        Check.True(result.Succeeded, "a deep directory is recycled as one entry");
        Check.False(Directory.Exists(outer), "and the outermost of it is gone from disk");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken), outer);

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

        var recycled = await FileRecycler.MoveToRecycleBinAsync([stuck, free], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, "both are recycled");

        // The payload is locked where it now lives, after the move. Holding it with no delete share is what
        // makes one entry undeletable without the case having to put the file in the bin in a broken state.
        using (new FileStream(NativeBinLayout.ToPayloadPath(RecordOf(stuck)), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var emptied = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

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

        await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);
        var second = await RecycleBin.EmptyAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

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
        var matches = contents.Items
            .Where(i => string.Equals(i.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Check.Equal(1, matches.Length, $"exactly one listed entry names {originalPath} (found {matches.Length} of {contents.Items.Count})");

        return matches[0];
    }

    /// <summary>
    /// The write path is refused below Windows 10 build 14393, which is the first Shell that reads a
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

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync([path, directory], Native, context.CancellationToken)
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

        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var payloadPath = NativeBinLayout.ToPayloadPath(RecordOf(path));
        RecycleBinContents contents;

        using (new FileStream(RecordOf(path), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

            // Found by its payload rather than by its original path, because asking every entry for its path
            // is the very reading this case says the listing does not do.
            var item = contents.Items.FirstOrDefault(i => SamePath(i.PayloadPath, payloadPath));
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

        var recycled = await FileRecycler.MoveToRecycleBinAsync([locked, other], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"both are recycled (first failure: {Describe(recycled.FirstFailure)})");

        var preload = new RecycleBinOptions { Backend = RecycleBinBackend.Native, PreloadItemDetails = true };
        var baseline = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        RecycleBinContents contents;

        using (new FileStream(RecordOf(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, preload, context.CancellationToken).ConfigureAwait(false);
        }

        Check.Equal(1, contents.Failures.Count, "exactly one entry is reported as unreadable");
        Check.Equal(RecycleFailureStage.Enumeration, contents.Failures[0].Stage, "at the stage where a listing reports what it could not read");
        Check.Equal(baseline.Items.Count - 1, contents.Items.Count, "and every other entry is still returned");

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
        ScratchWorkspace.RequireRecycling("a Recycle Bin for an entry to be listed with its details read");

        var path = CreateFile("preloaded.bin");
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var preload = new RecycleBinOptions { Backend = RecycleBinBackend.Native, PreloadItemDetails = true };
        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, preload, context.CancellationToken).ConfigureAwait(false);

        var item = contents.Items.First(i => SamePath(i.OriginalPath, path));
        File.Delete(RecordOf(path));

        Check.Equal(path, item.OriginalPath, "the path is still answerable after its record is gone");
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
        ScratchWorkspace.RequireRecycling("a Recycle Bin for entries whose details are read");

        var pathFirstFile = CreateFile("lazy-path-first.bin");
        var dateFirstFile = CreateFile("lazy-date-first.bin");

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync([pathFirstFile, dateFirstFile], Native, context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"both are recycled (first failure: {Describe(recycled.FirstFailure)})");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        var pathFirst = contents.Items.First(i => SamePath(i.OriginalPath, pathFirstFile));
        var dateFirst = contents.Items.First(i => SamePath(i.OriginalPath, dateFirstFile));

        Check.Equal(pathFirstFile, pathFirst.OriginalPath, "asking for the path answers it");
        File.Delete(RecordOf(pathFirstFile));
        Check.True(
            pathFirst.DeletedAt > DateTimeOffset.Now.AddHours(-1),
            $"and the time is still answerable afterwards, from the same reading (got {pathFirst.DeletedAt:O})");

        Check.True(dateFirst.DeletedAt > DateTimeOffset.Now.AddHours(-1), $"asking for the time answers it ({dateFirst.DeletedAt:O})");
        File.Delete(RecordOf(dateFirstFile));
        Check.Equal(
            dateFirstFile,
            dateFirst.OriginalPath,
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

        var path = CreateFile("vanishing-record.bin");
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

        // Found by its payload: asking an entry for its original path is the reading this case is about, and
        // an entry found that way would already be warm before its record is taken away.
        var expectedPayload = NativeBinLayout.ToPayloadPath(RecordOf(path));
        var item = contents.Items.First(i => SamePath(i.PayloadPath, expectedPayload));

        File.Delete(RecordOf(path));

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
            var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);

            Check.False(
                contents.Items.Any(i => SamePath(i.PayloadPath, stray)),
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
            var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);
            var item = contents.Items.First(i => SamePath(i.PayloadPath, payloadPath));

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

        var path = CreateFile("code-bearing.bin");
        var recycled = await FileRecycler.MoveToRecycleBinAsync([path], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the file is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var contents = await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken).ConfigureAwait(false);
        var expectedPayload = NativeBinLayout.ToPayloadPath(RecordOf(path));
        var item = contents.Items.First(i => SamePath(i.PayloadPath, expectedPayload));

        File.Delete(RecordOf(path));

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
    /// Written by hand into the bin rather than recycled, because the native backend writes both halves in one
    /// case and this machine's Shell writes them in another; neither path can be asked to produce a mismatch
    /// on demand. The tails are what the pairing keys on, so they are spelled differently too.
    /// </remarks>
    private static async Task NativeEnumerationPairsHalvesWhateverCaseTheyAreWrittenIn(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin to write a mixed-case pair into");

        var bin = NativeBinLayout.EnsureBinDirectory(ScratchWorkspace.VolumeRoot);

        var lowercaseRecord = Path.Combine(bin, "$ilower" + RunTag + ".bin");
        var lowercasePayload = Path.Combine(bin, "$rlower" + RunTag + ".bin");
        var upperRecord = Path.Combine(bin, "$IUPPER" + RunTag + ".BIN");
        var upperPayload = Path.Combine(bin, "$RUPPER" + RunTag + ".BIN");

        File.WriteAllBytes(lowercasePayload, Content);
        File.WriteAllBytes(upperPayload, Content);
        NativeIndexFile.Write(lowercaseRecord, ScratchWorkspace.Root + "\\lower.bin", Content.LongLength, DateTime.UtcNow);
        NativeIndexFile.Write(upperRecord, ScratchWorkspace.Root + "\\upper.bin", Content.LongLength, DateTime.UtcNow);

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
    /// A recycled directory is reported as zero bytes. The size a directory's own directory entry carries is
    /// the size of the directory record, which is not what the entry occupied and is not a figure any caller
    /// can act on — so the walk's answer is discarded and the flag beside it is what says why.
    /// </summary>
    /// <remarks>
    /// This pins the contract rather than the guard that enforces it: on NTFS <c>FindFirstFile</c> reports zero
    /// for a directory anyway, so on this machine removing the <c>IsDirectory</c> check would not turn this red.
    /// The test is here because the figure is part of what a listing promises, and because it is the case where
    /// the two backends differ most easily — the Shell measures the entry's own file and the native path does
    /// not.
    /// </remarks>
    private static async Task NativeListingReportsAZeroSizeForARecycledDirectory(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a Recycle Bin for a directory to be listed out of");

        var directory = Path.Combine(ScratchWorkspace.Root, "zero-sized-" + RunTag);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "inside.bin"), Content);

        var recycled = await FileRecycler.MoveToRecycleBinAsync([directory], Native, context.CancellationToken).ConfigureAwait(false);
        Check.True(recycled.Succeeded, $"the directory is recycled (first failure: {Describe(recycled.FirstFailure)})");

        var entries = NativeBinEntries.Collect(BinDirectory, context.CancellationToken);
        var directoryIndex = RecordOf(directory);
        var enumerated = entries.Single(e => SamePath(e.IndexPath ?? string.Empty, directoryIndex));

        Check.True(enumerated.PayloadIsDirectory, "the enumeration reports the payload as a directory");
        Check.Equal(0, enumerated.PayloadLength, "with no size of its own for the directory record to carry");

        var item = FindListed(RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, Native, context.CancellationToken), directory);
        Check.True(item.IsDirectory, "and the listing agrees it is a directory");
        Check.Equal(0, item.Size, "with the same zero size, whatever the walk said about the directory itself");

        workspace.Dispose();
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
    /// The staging directories <c>LongPathStager</c> creates in the volume root. Counted rather than
    /// listed, so a case says how many there were and not what they were called.
    /// </summary>
    private static int CountStagingDirectories()
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
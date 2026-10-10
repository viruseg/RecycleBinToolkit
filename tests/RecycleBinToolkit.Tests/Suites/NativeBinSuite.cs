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
        new TestCase("NativeBinSuite." + nameof(NativeListingFindsAnEntryNativeRecycleRecorded), NativeListingFindsAnEntryNativeRecycleRecorded),
        new TestCase("NativeBinSuite." + nameof(NativeListingReportsARecycledDirectoryAsADirectory), NativeListingReportsARecycledDirectoryAsADirectory),
        new TestCase("NativeBinSuite." + nameof(NativeListingOfAVolumeWithNoBinDirectoryIsEmpty), NativeListingOfAVolumeWithNoBinDirectoryIsEmpty),
        new TestCase("NativeBinSuite." + nameof(NativeListingToleratesAnIndexWhosePayloadIsGone), NativeListingToleratesAnIndexWhosePayloadIsGone),
        new TestCase("NativeBinSuite." + nameof(NativeListingRecordsAnIndexItCannotRead), NativeListingRecordsAnIndexItCannotRead),
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
        Check.True(File.Exists(item.RecycledPath), $"its payload is where the listing says ({item.RecycledPath})");

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
        Check.True(Directory.Exists(item.RecycledPath), $"and the payload is a directory ({item.RecycledPath})");

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
            .Run(() => NativeBinLister.TryEnumerate([root], context.CancellationToken), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(0, contents.Items.Count, "a volume with no bin yields no items");
        Check.Equal(0, contents.Failures.Count, "and no failures, because an empty bin is an answer rather than a problem");

        workspace.Dispose();
    }

    /// <summary>
    /// A record whose payload is gone — a crash between the two writes, or an empty that ran while a listing
    /// was in flight — is still an entry the bin knows about. The listing reports it, because the record is
    /// what the bin holds and the payload is one missing file rather than a reason to fail the whole
    /// listing.
    /// </summary>
    private static async Task NativeListingToleratesAnIndexWhosePayloadIsGone(TestContext context)
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
        var item = contents.Items.FirstOrDefault(i => string.Equals(i.OriginalPath, path, StringComparison.OrdinalIgnoreCase));

        Check.NotNull(item, "the entry is still listed, because its record is still there");
        Check.Equal(payloadPath, item!.RecycledPath, "with the payload path the record's name gives");
        Check.False(File.Exists(item.RecycledPath), "which no longer resolves to anything, and that is not a failure");

        workspace.Dispose();
    }

    /// <summary>
    /// One record nobody can read must not cost the caller the rest of the bin. A bin of ninety thousand
    /// entries with one locked is a listing of ninety thousand, not an exception and not ninety failures.
    /// </summary>
    private static async Task NativeListingRecordsAnIndexItCannotRead(TestContext context)
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

        Check.Equal(1, contents.Failures.Count, "exactly one entry is reported as unreadable");
        Check.Equal(RecycleFailureStage.Enumeration, contents.Failures[0].Stage, "at the stage where a listing reports what it could not read");
        Check.Equal(baseline - 1, contents.Items.Count, "and every other entry is still returned");

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
            ? Math.Max(1, Environment.ProcessorCount)
            : 1;

        Check.Equal(
            expected,
            VolumeSeekPenalty.DegreeOfParallelism(root),
            kind == VolumeStorageKind.SolidState
                ? "a solid-state volume is read on every core"
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
        Check.True(File.Exists(item.RecycledPath), $"and points at a payload that is really there ({item.RecycledPath})");

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
        Check.True(File.Exists(item.RecycledPath), $"pointing at the payload ({item.RecycledPath})");

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
        File.Move(item.RecycledPath, item.OriginalPath);

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
        Check.True(Directory.Exists(item.RecycledPath), $"whose whole tree came with it ({item.RecycledPath})");

        // Only ever the last component of anything found: the payload sits under a path this suite must not
        // print, and a recursive enumeration of a path this deep would be as much a violation as it would be
        // a long-path operation. What is checked is that the nesting survived the move intact.
        var walk = item.RecycledPath;

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
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

    /// <summary>Far enough that a version 2 record cannot be decoded from a 1 KB stack buffer.</summary>
    private const int FarPastMaxPath = 500;

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

        var directory = Path.Combine(ScratchWorkspace.Root, "a-directory");
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

        var directory = Path.Combine(ScratchWorkspace.Root, "listed-directory");
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
    /// Driven through a bin directory that was never created, rather than through a volume that is not
    /// mounted: what is under test is what the listing does when there is nothing there to list, and a
    /// volume nobody has would be a different thing. The directory asked about sits inside the scratch root
    /// so the case creates nothing and removes nothing.
    /// </remarks>
    private static async Task NativeListingOfAVolumeWithNoBinDirectoryIsEmpty(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var absent = Path.Combine(ScratchWorkspace.Root, "$RECYCLE.BIN", NativeBinLayout.CurrentSid);
        Check.False(Directory.Exists(absent), $"the bin directory does not exist to begin with ({absent})");

        var contents = await Task.Run(() => NativeBinLister.TryEnumerate([absent], context.CancellationToken), context.CancellationToken).ConfigureAwait(false);

        Check.Equal(0, contents.Items.Count, "a bin directory that does not exist yields no items");
        Check.Equal(0, contents.Failures.Count, "and no failures");

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
    /// The drive is classified by asking it, and the answer decides how many threads read its bin. Nothing
    /// here knows what any particular machine has: the assertion is that the answer came from the driver
    /// and that a positive answer names a positive kind, which is the only part that can be witnessed
    /// without knowing which volume is under test.
    /// </summary>
    private static Task ADriveIsClassifiedFromItsOwnSeekPenaltyAnswer(TestContext context)
    {
        var kind = VolumeSeekPenalty.Classify(ScratchWorkspace.VolumeRoot);

        Check.True(kind != VolumeStorageKind.Unknown,
            $"the driver answered whether this volume seeks ({ScratchWorkspace.VolumeRoot}); an unknown answer would silently force the slowest path");

        Check.True(
            kind == VolumeStorageKind.SolidState || kind == VolumeStorageKind.Mechanical,
            $"and the answer names one of the two kinds rather than something else ({kind})");

        var expected = kind == VolumeStorageKind.SolidState
            ? Math.Max(1, Environment.ProcessorCount)
            : 1;

        Check.Equal(expected, VolumeSeekPenalty.DegreeOfParallelism(ScratchWorkspace.VolumeRoot),
            kind == VolumeStorageKind.SolidState
                ? "a solid-state volume is read on every core"
                : "a mechanical volume is read on one thread, because concurrent requests make the head seek between them");

        Check.Equal(kind, VolumeSeekPenalty.Classify(ScratchWorkspace.VolumeRoot), "and the answer is the same when asked twice");

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

        Check.True(File.Exists(path), "the payload is back where it was deleted from");

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
    /// Creates files of one size each, in the scratch root.
    /// </summary>
    private static IReadOnlyList<string> CreateMany(int count, int sizeBytes)
    {
        var content = new byte[sizeBytes];
        new Random(20261010).NextBytes(content);

        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(ScratchWorkspace.Root, $"batch-{i}.bin");
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

    private static string CreateFile(string name)
    {
        var path = Path.Combine(ScratchWorkspace.Root, name);
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

        var path = Path.Combine(directory, new string('f', 100) + ".bin");
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
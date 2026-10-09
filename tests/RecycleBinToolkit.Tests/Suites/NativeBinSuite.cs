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
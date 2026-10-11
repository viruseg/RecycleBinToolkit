using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class RestoreSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("RestoreSuite." + nameof(DefaultsAreTheDocumentedOnes), DefaultsAreTheDocumentedOnes),
        new TestCase("RestoreSuite." + nameof(VolumesOnOneDiskShareOneKeyAndAnotherDiskDoesNot), VolumesOnOneDiskShareOneKeyAndAnotherDiskDoesNot),
        new TestCase("RestoreSuite." + nameof(EveryItemIsSignalledOnceAndPreloadedOnesCarryNoCode), EveryItemIsSignalledOnceAndPreloadedOnesCarryNoCode),
        new TestCase("RestoreSuite." + nameof(RenamingTakesTheFirstFreeNumberBeforeTheExtension), RenamingTakesTheFirstFreeNumberBeforeTheExtension),
        new TestCase("RestoreSuite." + nameof(ThePlannerSaysWhatIsInTheWayAndWhatToDoAboutIt), ThePlannerSaysWhatIsInTheWayAndWhatToDoAboutIt)
    ];

    /// <summary>
    /// The shapes a restore reports in are public and have no behaviour behind them yet, so what this
    /// pins is every value a caller can already see: the two conflict policies and their defaults, the
    /// numbers behind the two restore stages, and what a result carries back.
    /// </summary>
    /// <remarks>
    /// The normalisation half is the one with teeth. <see cref="RecycleBinOptions.Normalize"/> copies
    /// field by field, so an option it does not name is silently reset to the property initialiser's
    /// default on every internal call — a caller who asked for one behaviour would get another, with
    /// nothing raised and nothing reported.
    /// <para>
    /// The result half is read list by list rather than through its totals, because a constructor that
    /// swapped two of them would answer every count correctly. <see cref="RecycleBinRestoreResult.Succeeded"/>
    /// is read on both of its branches: a conflict alone must leave it true, which is what the remarks on
    /// it promise.
    /// </para>
    /// </remarks>
    private static Task DefaultsAreTheDocumentedOnes(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        Check.Equal(RestoreDirectoryConflict.MergeSkip, RecycleBinOptions.Default.DirectoryConflict, "the directory default");
        Check.Equal(RestoreFileConflict.Skip, RecycleBinOptions.Default.FileConflict, "the file default");

        // Normalize copies fields one by one; a field it does not mention is dropped on every internal call.
        var normalized = new RecycleBinOptions
        {
            DirectoryConflict = RestoreDirectoryConflict.MergeReplace,
            FileConflict = RestoreFileConflict.Rename
        }.Normalize();

        Check.Equal(RestoreDirectoryConflict.MergeReplace, normalized.DirectoryConflict, "directory policy survives normalization");
        Check.Equal(RestoreFileConflict.Rename, normalized.FileConflict, "file policy survives normalization");

        Check.Equal(10, (int)RecycleFailureStage.RestoreFailed, "RestoreFailed keeps clear of the removed Shell stages");
        Check.Equal(11, (int)RecycleFailureStage.RestoreConflictRefused, "RestoreConflictRefused follows it");

        var conflict = new RecycleRestoreConflict("b", true, RecycleRestoreConflictAction.Skipped);
        Check.Equal(0, (int)RecycleRestoreConflictAction.Skipped, "Skipped keeps number 0");
        Check.Equal(1, (int)RecycleRestoreConflictAction.RestoredRenamed, "RestoredRenamed takes number 1");
        Check.Equal(2, (int)RecycleRestoreConflictAction.MergedSkipped, "MergedSkipped takes number 2");
        Check.Equal(3, (int)RecycleRestoreConflictAction.MergedReplaced, "MergedReplaced takes number 3");
        Check.Equal(4, (int)RecycleRestoreConflictAction.MergedRenamed, "MergedRenamed takes number 4");
        Check.Equal(
            5,
            Enum.GetValues<RecycleRestoreConflictAction>().Length,
            "five actions, so a value that only described a directory's merge cannot come back under a borrowed name");

        var result = new RecycleBinRestoreResult(["a"], [conflict], [], 1, 42, TimeSpan.FromMilliseconds(3));
        Check.Equal(1, result.ItemsRestored, "one item restored");
        Check.Equal(42, result.RestoredSize, "its bytes");
        Check.Equal(1, result.RestoredPaths.Count, "the restored path is carried");
        Check.Equal("a", result.RestoredPaths[0], "and it is the one handed in, not a path invented here");
        Check.Equal(1, result.Conflicts.Count, "the conflict is carried");
        Check.True(ReferenceEquals(conflict, result.Conflicts[0]), "and it is the one handed in");
        Check.Equal(0, result.Failures.Count, "the failures list is carried, empty");
        Check.True(result.Succeeded, "nothing failed");
        Check.Equal(3, result.Elapsed.TotalMilliseconds, "elapsed carries");

        // A conflict alone does not make the result fail, so the false branch is only reachable through
        // a failure — and through any of them, whatever the reason recorded on it.
        var failed = new RecycleBinRestoreResult([], [conflict], [new RecycleFailure("c", 0, 1, RecycleFailureStage.RestoreFailed)], 0, 0, TimeSpan.Zero);
        Check.False(failed.Succeeded, "one failure makes the result not have succeeded");
        Check.Equal(1, failed.Failures.Count, "and the failure is carried");

        workspace.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Two volumes on one disk get one key and a volume on another gets another, which is the whole of what
    /// grouping by disk is for: the second pair of volumes can be restored beside the first, and two volumes
    /// sharing a key cannot be.
    /// </summary>
    /// <remarks>
    /// The drive letters are data and nothing is created, recycled, moved or deleted on any of them, exactly
    /// as the cases that put <c>W:\</c> into a path predicate do. The answer comes from the driver through
    /// the volume's own device, so a volume whose driver declines leaves nothing to assert about it here.
    /// <para>
    /// <c>C:</c> is asserted against rather than <c>D:</c> alone because the claim that matters is the
    /// asymmetry: the key is not a per-volume identity, or the two names would differ for a reason that has
    /// nothing to do with where the bytes are.
    /// </para>
    /// <para>
    /// Which volumes sit on which disk is a fact about this machine and nothing here can arrange it, so a
    /// machine without these three drives is a machine this case does not run on. What it asks about is
    /// three named volumes, which is why <see cref="RequireVolumePresent"/> stands in for them. When they
    /// are all here the assertions are the claim and they stand unweakened: a machine where <c>D:</c> and
    /// <c>E:</c> are not one disk fails this case rather than redefining it.
    /// </para>
    /// </remarks>
    private static Task VolumesOnOneDiskShareOneKeyAndAnotherDiskDoesNot(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        RequireVolumePresent('D');
        RequireVolumePresent('E');
        RequireVolumePresent('C');

        // Data, not targets: nothing is created, recycled, moved or deleted on any of these paths, exactly as
        // the existing cases that put W:\ into a path predicate do.
        Check.True(PhysicalVolumeDisk.TryIdentify(@"D:\", out var d), "D: answered which disk it sits on");
        Check.True(PhysicalVolumeDisk.TryIdentify(@"E:\", out var e), "E: answered");
        Check.True(PhysicalVolumeDisk.TryIdentify(@"C:\", out var c), "C: answered");

        Check.Equal(d, e, "D: and E: are one physical disk, so they must key the same");
        Check.True(d != c, "C: is a different physical disk, so it must not");

        Check.False(PhysicalVolumeDisk.TryIdentify(@"\\server\share\", out _), "a share has no local volume device and must not answer");

        workspace.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>
    /// The loader says each entry as it reads its record, and says nothing for an entry it did not
    /// read — which is why the two are asserted against the same list: an entry a listing already
    /// warmed is skipped by the grouping, so the loader never reaches it, and the engine's own
    /// pass over the list is what reports it.
    /// </summary>
    /// <remarks>
    /// The claim is "once each", so a single call that must yield exactly one report per entry is
    /// stronger than a count. The list is also the only thing that pins which entry each report is
    /// about: an implementation that reported the same entry three times, or three entries in the
    /// wrong order, would answer both counts correctly.
    /// <para>
    /// The bin is whatever this machine has put in it, so the entries this case recycled are told
    /// from the rest by taking the listing before and after rather than by assuming an empty volume.
    /// Filtering on the original path is not available: reading it is what warms an entry, so asking
    /// which entries are this case's would make them all preloaded and leave the first half with
    /// nothing to read.
    /// </para>
    /// <para>
    /// The unreadable entry is built rather than made, for the reason
    /// <c>NativeBinSuite</c> gives for building its own: taking a record away from a real bin leaves
    /// a payload in the user's Recycle Bin with nothing that will ever collect it. Nothing is
    /// written and nothing is deleted here at all.
    /// </para>
    /// </remarks>
    private static async Task EveryItemIsSignalledOnceAndPreloadedOnesCarryNoCode(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries to load");

        var volumeRoot = ScratchWorkspace.VolumeRoot;

        var before = Listing.OnlyEntries(
            await RecycleBin.GetItemsAsync(volumeRoot, cancellationToken: context.CancellationToken),
            volumeRoot).Select(item => item.PayloadPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = workspace.CreateRandomFiles(3, 4096);
        var recycled = await RecycleBin.RecycleAsync(files, cancellationToken: context.CancellationToken);
        Check.True(recycled.Succeeded, "the files reached the bin");

        var listed = Listing.OnlyEntries(
            await RecycleBin.GetItemsAsync(volumeRoot, cancellationToken: context.CancellationToken),
            volumeRoot);
        var cold = listed.Where(item => !before.Contains(item.PayloadPath)).ToArray();

        Check.Equal(3, cold.Length, "three entries came back");
        Check.False(cold.Any(item => item.IsDetailsLoaded), "and a listing that asked for no preloading read none of them");

        var read = new CollectingSignal();

        // One call over all four, so "once each" is asserted against a list that mixes an entry
        // whose record is not there among the three whose records are. An entry whose record cannot
        // be read is still an entry the caller has to hear about, so it is signalled with the code
        // the read raised rather than skipped.
        //
        // Nothing is written and nothing is deleted for that entry, and nothing outside the scratch
        // root is named: the path sits under the scratch root precisely because Path.GetPathRoot on
        // it is what puts it in the group alongside the other three.
        var missing = new RecycleBinItem(
            Path.Combine(ScratchWorkspace.Root, "never-written-record.bin"),
            Path.Combine(ScratchWorkspace.Root, "never-written-payload.bin"),
            0,
            false);

        var withUnreadable = new[] { cold[0], missing, cold[1], cold[2] };

        ItemDetailLoader.Load(withUnreadable, context.CancellationToken, in read);

        Check.Equal(4, read.Seen, "every entry the loader read is signalled, once each");
        Check.Equal(3, read.Items.Count(h => h.HResult == 0), "a record that reads carries no code: nothing was wrong with it");
        Check.Equal(1, read.Items.Count(h => h.HResult != 0), "and the one that could not be read carries the code the read raised");

        for (var index = 0; index < withUnreadable.Length; index++)
        {
            Check.True(
                read.Items.Any(h => ReferenceEquals(h.Item, withUnreadable[index])),
                $"the entry at index {index} is the one reported about it");
        }

        // Every entry is loaded now, which is the state a listing taken with PreloadItemDetails
        // leaves them in. The loader skips them all and reads nothing, so the engine's own pass is
        // the only thing reporting them — and it has no code to report, because there was no read.
        Check.True(cold.All(item => item.IsDetailsLoaded), "the loader left every one of them read");

        var passed = new CollectingSignal();

        for (var index = 0; index < cold.Length; index++)
        {
            if (cold[index].IsDetailsLoaded)
            {
                passed.OnReady(index, cold[index], 0);
            }
        }

        ItemDetailLoader.Load(cold, context.CancellationToken, in passed);

        Check.Equal(3, passed.Seen, "the two passes together report every entry exactly once");
        Check.Equal(0, passed.Items.Count(h => h.HResult != 0), "an entry the listing already read carries no code");

        workspace.Dispose();
    }

    /// <summary>
    /// A name nobody is using becomes the same name with <c>(1)</c> in front of the extension, and a name
    /// somebody is using becomes the same name with the next number nobody is using.
    /// </summary>
    /// <remarks>
    /// The convention is what the public options promise — <see cref="RestoreFileConflict.Rename"/> writes it
    /// out in prose, and this is the only code behind it — so what is pinned here is the spelling, not the
    /// arithmetic: where the number goes, where a double extension splits, and that the counting starts at one.
    /// <para>
    /// The number is never read back out of the name it produced. <c>report (5).txt</c> is a name, not a
    /// statement about how many reports there are, so it becomes <c>report (5) (1).txt</c> when taken — a
    /// parser that looked for the bracketed digits would answer <c>report (5) (6).txt</c> instead, and would
    /// pick a name that is already gone. What is asserted about an already-numbered name is that it is not
    /// re-parsed.
    /// </para>
    /// <para>
    /// A name that is nothing but an extension — <c>.gitignore</c> — is the one case where there is no stem
    /// to put the number in front of, and the number goes after the whole name rather than in front of an
    /// empty one.
    /// </para>
    /// <para>
    /// The last shape is the one the buffer itself decides: a candidate is assembled on the stack for an
    /// ordinary path and on the heap for one too long for that, and the two arms have to answer identically.
    /// A single flat component cannot reach the heap arm from this root — 255 characters is the most one
    /// component may be, and what the scratch root itself uses leaves less room than that — so the deep
    /// path is built from nested components, which is what a deep tree on a long-path volume produces
    /// anyway. Its length is asserted rather than assumed, so the case cannot quietly stop covering the arm
    /// it is written for.
    /// </para>
    /// </remarks>
    private static Task RenamingTakesTheFirstFreeNumberBeforeTheExtension(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var original = workspace.CreateRandomFile(64);
        var originalName = Path.GetFileName(original);

        var first = RestorePathPlanner.WithFreeNumber(original);

        Check.Equal(
            Path.Combine(ScratchWorkspace.Root, Path.GetFileNameWithoutExtension(originalName) + " (1)" + Path.GetExtension(originalName)),
            first,
            "with the name free, the first number is 1");

        // The squatter is the name the first call answered, not the name it was asked about: the number
        // counts what is taken among the numbered names, and the one just handed out is the one taken.
        File.WriteAllText(first, "occupied");

        Check.Equal(
            Path.Combine(ScratchWorkspace.Root, Path.GetFileNameWithoutExtension(originalName) + " (2)" + Path.GetExtension(originalName)),
            RestorePathPlanner.WithFreeNumber(original),
            "once taken, the next number is 2");

        // A double extension is split at the last dot, which is what Path.GetExtension says.
        Check.Equal("archive.tar (1).gz", Path.GetFileName(RestorePathPlanner.WithFreeNumber(Path.Combine(ScratchWorkspace.Root, "archive.tar.gz"))), "the number goes before the extension");

        Check.True(RestorePathPlanner.WithFreeNumber(Path.Combine(ScratchWorkspace.Root, "noext")).EndsWith("noext (1)", StringComparison.Ordinal), "a name with no extension still takes a number");

        // Nothing is re-parsed, so an extension that is itself a number is just an extension and a name
        // that already carries one gains a second rather than counting on from the first.
        Check.True(RestorePathPlanner.WithFreeNumber(Path.Combine(ScratchWorkspace.Root, "report.2")).EndsWith("report (1).2", StringComparison.Ordinal), "a numeric extension is an extension, not a number");
        Check.True(RestorePathPlanner.WithFreeNumber(first).EndsWith("(1) (1).bin", StringComparison.Ordinal), "a name already carrying a number is not read back as one");

        // Path.GetExtension reads ".gitignore" as an extension with no stem in front of it, so a name that
        // is nothing but an extension would otherwise be numbered into " (1).gitignore".
        Check.Equal(".gitignore (1)", Path.GetFileName(RestorePathPlanner.WithFreeNumber(Path.Combine(ScratchWorkspace.Root, ".gitignore"))), "a name that is only an extension takes the number after the whole name");

        // The name is assembled in a buffer on the stack for an ordinary path and on the heap for one too
        // long for that, and the two have to answer the same thing. One flat component cannot reach the
        // heap arm from this root — 255 characters is the most a name may be, and the root is shorter than
        // that leaves room — so the path is built from nested components, which is what a deep tree on a
        // volume with long paths enabled actually produces.
        var component = new string('n', 200);
        var deep = Path.Combine(ScratchWorkspace.Root, component, component);
        Directory.CreateDirectory(deep);

        var longPath = Path.Combine(deep, "leaf.bin");
        File.WriteAllText(longPath, "occupied");

        var numbered = RestorePathPlanner.WithFreeNumber(longPath);

        Check.Equal("leaf (1).bin", Path.GetFileName(numbered), "a path too long for the stack buffer still takes its number before the extension");
        Check.Equal(Path.GetDirectoryName(longPath), Path.GetDirectoryName(numbered), "and it lands in the same directory, however deep that is");
        Check.True(longPath.Length > 320, $"the path really did leave the stack buffer: {longPath.Length} characters");

        return Task.CompletedTask;
    }

    /// <summary>
    /// What is in the way of a path is asked once and answered twice: as the action, and as the kind of
    /// occupant, which is the half of the answer <see cref="RecycleRestoreConflict"/> reports to a caller.
    /// </summary>
    /// <remarks>
    /// A destination holding the other kind is a conflict like any other, so both cross-kind cases are here:
    /// a file arriving where a directory is, and the reverse. Asserting them is what stops the planner from
    /// answering <see cref="RestoreAction.Move"/> to a path that is occupied by something.
    /// <para>
    /// The three merge modes answer <see cref="RestoreAction.Move"/> rather than a fourth action. A directory
    /// is never replaced whole and never silently kept away from what it is restoring into, so what a merge
    /// needs is permission to put the tree at that path — which is what moving there means — and
    /// <see cref="RestorePathPlanner.DecideMerged"/> then decides what happens to each file inside it.
    /// </para>
    /// <para>
    /// <see cref="RestoreDirectoryConflict.Error"/> and <see cref="RestoreFileConflict.Error"/> answer
    /// <see cref="RestoreAction.Skip"/>: whether the item also lands in the failures is the caller's answer
    /// from the options, and an action is not a place to put it. An enum value outside either policy reaches
    /// the same arm, which is the one that touches nothing.
    /// </para>
    /// </remarks>
    private static Task ThePlannerSaysWhatIsInTheWayAndWhatToDoAboutIt(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var free = Path.Combine(ScratchWorkspace.Root, "free.bin");
        var takenByFile = Path.Combine(ScratchWorkspace.Root, "taken.bin");
        var takenByDirectory = Path.Combine(ScratchWorkspace.Root, "taken");

        File.WriteAllText(takenByFile, "a file");
        Directory.CreateDirectory(takenByDirectory);

        var defaults = new RecycleBinOptions();

        var freeFile = RestorePathPlanner.Decide(free, isDirectory: false, defaults);
        Check.Equal(RestoreAction.Move, freeFile.Action, "a free path is moved to");
        Check.Equal(free, freeFile.TargetPath, "and it lands where it wanted to");
        Check.False(freeFile.OccupiedByDirectory, "nothing is in the way of a free path");

        Check.Equal(RestoreAction.Move, RestorePathPlanner.Decide(free, isDirectory: true, defaults).Action, "a free path is the same answer for a directory");

        var replaced = RestorePathPlanner.Decide(takenByFile, isDirectory: false, new RecycleBinOptions { FileConflict = RestoreFileConflict.Replace });
        Check.Equal(RestoreAction.Replace, replaced.Action, "the file policy replaces");
        Check.Equal(takenByFile, replaced.TargetPath, "and it lands on the path that was occupied");
        Check.False(replaced.OccupiedByDirectory, "what is in the way is a file");

        var renamed = RestorePathPlanner.Decide(takenByFile, isDirectory: false, new RecycleBinOptions { FileConflict = RestoreFileConflict.Rename });
        Check.Equal(RestoreAction.Rename, renamed.Action, "the file policy renames");
        Check.Equal(RestorePathPlanner.WithFreeNumber(takenByFile), renamed.TargetPath, "and it lands under the first free name, which is the one naming gives");
        Check.False(renamed.OccupiedByDirectory, "renaming does not change what was in the way");

        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByFile, isDirectory: false, defaults).Action, "the default file policy skips");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByFile, isDirectory: false, new RecycleBinOptions { FileConflict = RestoreFileConflict.Error }).Action, "and refusing is the same action as skipping");

        var renamedDirectory = RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.RenameDirectory });
        Check.Equal(RestoreAction.Rename, renamedDirectory.Action, "renaming a directory takes a free name rather than merging into what is there");
        Check.Equal(RestorePathPlanner.WithFreeNumber(takenByDirectory), renamedDirectory.TargetPath, "and it lands under the first free name");
        Check.True(renamedDirectory.OccupiedByDirectory, "a directory was in the way");

        Check.Equal(RestoreAction.Move, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, defaults).Action, "the default directory policy merges rather than skipping");
        Check.Equal(RestoreAction.Move, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.MergeSkip }).Action, "and every merge mode answers the same");
        Check.Equal(RestoreAction.Move, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.MergeReplace }).Action, "all three");
        Check.Equal(RestoreAction.Move, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.MergeRename }).Action, "all three of them");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.Skip }).Action, "while leaving the directory in the bin is a skip");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.Error }).Action, "and refusing is the same action as skipping");

        // The cross-kind cases: each is a conflict like any other, and the policy that answers is the one
        // for the kind arriving rather than the one for the kind in the way.
        var fileIntoDirectory = RestorePathPlanner.Decide(takenByDirectory, isDirectory: false, new RecycleBinOptions { FileConflict = RestoreFileConflict.Rename });
        Check.Equal(RestoreAction.Rename, fileIntoDirectory.Action, "a file arriving at a directory is renamed rather than moved over it");
        Check.True(fileIntoDirectory.OccupiedByDirectory, "and the directory it found is reported as a directory");

        var directoryIntoFile = RestorePathPlanner.Decide(takenByFile, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.RenameDirectory });
        Check.Equal(RestoreAction.Rename, directoryIntoFile.Action, "a directory arriving at a file takes a free name");
        Check.False(directoryIntoFile.OccupiedByDirectory, "and the file it found is not a directory");

        // An enum value outside either policy is reachable by an unvalidated caller and lands on the arm
        // that touches nothing, rather than on a default that could move something.
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByFile, isDirectory: false, new RecycleBinOptions { FileConflict = (RestoreFileConflict)42 }).Action, "a file policy that names nothing answers skip");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = (RestoreDirectoryConflict)42 }).Action, "and so does a directory policy");

        // What a merge decides per file, against the three modes that reach it and one that never does.
        Check.Equal(RestoreAction.Replace, RestorePathPlanner.DecideMerged(takenByFile, RestoreDirectoryConflict.MergeReplace), "a merge that replaces overwrites the file in the way");
        Check.Equal(RestoreAction.Rename, RestorePathPlanner.DecideMerged(takenByFile, RestoreDirectoryConflict.MergeRename), "a merge that renames sets the file down beside it");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.DecideMerged(takenByFile, RestoreDirectoryConflict.MergeSkip), "a merge that skips leaves the file in the way alone");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.DecideMerged(takenByFile, RestoreDirectoryConflict.RenameDirectory), "a value that is decided before the walk begins and never during it is answered as skip");

        // Nothing above wrote to the Recycle Bin or moved anything: the only names on disk are the two
        // this case put there, and what is in the way is still what was put there.
        Check.Equal("a file", File.ReadAllText(takenByFile), "the file in the way was not touched");
        Check.True(Directory.Exists(takenByDirectory), "and neither was the directory");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Keeps what it was told, so a case can read it back after the call returns.
    /// </summary>
    /// <remarks>
    /// The loader reads one volume's records on as many threads as that volume answers for and calls
    /// this from each of them, so the list is written under a lock rather than appended to.
    /// </remarks>
    private readonly struct CollectingSignal : IRestoreSignal
    {
        private readonly List<(RecycleBinItem Item, int HResult)> _collected;

        public CollectingSignal()
        {
            _collected = [];
        }

        /// <summary>How many entries have been reported.</summary>
        internal int Seen
        {
            get
            {
                lock (_collected)
                {
                    return _collected.Count;
                }
            }
        }

        /// <summary>What was reported, in the order the loader reached it.</summary>
        internal List<(RecycleBinItem Item, int HResult)> Items
        {
            get
            {
                lock (_collected)
                {
                    return [.. _collected];
                }
            }
        }

        public void OnReady(int index, RecycleBinItem item, int hResult)
        {
            lock (_collected)
            {
                _collected.Add((item, hResult));
            }
        }
    }

    /// <summary>
    /// Ends the calling case as not run when <paramref name="letter"/> is not a volume this machine has.
    /// </summary>
    /// <remarks>
    /// The volumes this case compares are named in the source, so what it needs is a fact about the machine
    /// rather than anything the run can arrange — and a case that read a machine's partition layout as a
    /// defect would be the repository's rule against asking a run to be the right kind of disk, with the
    /// letters as the "right kind".
    /// <para>
    /// Presence is read from the root resolving rather than from <see cref="DriveInfo"/> alone, for the
    /// reason <see cref="AbsentVolume.RequireAbsent"/> checks both ways: an enumeration omits roots Windows
    /// still resolves, and a root that resolves is a volume whichever way the enumeration reads.
    /// </para>
    /// </remarks>
    /// <param name="letter">The letter the case needs to be able to ask about.</param>
    /// <exception cref="CheckSkippedException">
    /// Nothing is mounted on <paramref name="letter"/>. The message names the volume and what the case
    /// needed from it, and reads as a fact about this machine.
    /// </exception>
    private static void RequireVolumePresent(char letter)
    {
        var root = AbsentVolume.RootOf(letter);

        if (!Directory.Exists(root))
        {
            Check.Skip(
                $"{root} is not a volume on this machine. This case needs D:, E: and C: all present, "
                + "to read which physical disk each one sits on");
        }
    }
}

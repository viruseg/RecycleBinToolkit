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
        new TestCase("RestoreSuite." + nameof(ThePlannerSaysWhatIsInTheWayAndWhatToDoAboutIt), ThePlannerSaysWhatIsInTheWayAndWhatToDoAboutIt),
        new TestCase("RestoreSuite." + nameof(ARecycledFileComesBackToThePathItCameFrom), ARecycledFileComesBackToThePathItCameFrom),
        new TestCase("RestoreSuite." + nameof(ATakenFilePathIsSkippedAndTheEntryStaysInTheBin), ATakenFilePathIsSkippedAndTheEntryStaysInTheBin),
        new TestCase("RestoreSuite." + nameof(ATakenFilePathIsReplacedAndTheEntryLeavesTheBin), ATakenFilePathIsReplacedAndTheEntryLeavesTheBin),
        new TestCase("RestoreSuite." + nameof(ATakenFilePathIsRestoredUnderAFreeName), ATakenFilePathIsRestoredUnderAFreeName),
        new TestCase("RestoreSuite." + nameof(ATakenFilePathIsRefusedAsBothAConflictAndAFailure), ATakenFilePathIsRefusedAsBothAConflictAndAFailure),
        new TestCase("RestoreSuite." + nameof(ADirectoryComesBackWithItsTree), ADirectoryComesBackWithItsTree),
        new TestCase("RestoreSuite." + nameof(ADirectoryComesBackUnderParentsThatAreGone), ADirectoryComesBackUnderParentsThatAreGone),
        new TestCase("RestoreSuite." + nameof(AFileComesBackUnderAParentThatIsGone), AFileComesBackUnderAParentThatIsGone)
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
    /// A name is free when nothing at all is using it. Each of the three shapes below is a name whose occupancy
    /// is of the kind its own name does not suggest: a directory whose name carries a dot, a file whose
    /// numbered name is held by a directory, and an extension-less name whose numbered name is held by a
    /// file. A probe that asked only whether there was a file — or only whether there was a directory —
    /// would hand back the occupied name in every one of them, and the move that follows would throw on a
    /// name this library picked itself.
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

        // A name is taken by whatever kind holds it, and the number must step over that as readily as over a
        // same-kind occupant. A probe that asked only "is there a file at this name" would answer these three
        // with the occupied name itself, because File.Exists is false of a directory and Directory.Exists is
        // false of a file — and the move that follows would then throw on a name this naming chose itself.
        //
        // Each squatter goes where this naming actually puts the number, which for the dotted name is before
        // its extension rather than after the whole thing: "Photos.2024" numbers to "Photos (1).2024".
        var dottedDirectory = Path.Combine(ScratchWorkspace.Root, "Photos.2024");
        Directory.CreateDirectory(Path.Combine(ScratchWorkspace.Root, "Photos (1).2024"));

        Check.Equal(
            "Photos (2).2024",
            Path.GetFileName(RestorePathPlanner.WithFreeNumber(dottedDirectory)),
            "a directory whose name carries a dot is not probed as a file");

        var report = Path.Combine(ScratchWorkspace.Root, "report.txt");
        Directory.CreateDirectory(Path.Combine(ScratchWorkspace.Root, "report (1).txt"));

        Check.Equal(
            "report (2).txt",
            Path.GetFileName(RestorePathPlanner.WithFreeNumber(report)),
            "a name taken by a directory is not free for a file");

        var readme = Path.Combine(ScratchWorkspace.Root, "README");
        File.WriteAllText(Path.Combine(ScratchWorkspace.Root, "README (1)"), "a file");

        Check.Equal(
            "README (2)",
            Path.GetFileName(RestorePathPlanner.WithFreeNumber(readme)),
            "a name with no extension is not probed as a directory either");

        // Skipping a name the other kind holds costs one extra query and cannot be wrong the way returning
        // one is, so the two probes run together rather than one chosen from the name.
        Check.True(Directory.Exists(Path.Combine(ScratchWorkspace.Root, "Photos (1).2024")), "the directory that was stepped over is still there");
        Check.True(Directory.Exists(Path.Combine(ScratchWorkspace.Root, "report (1).txt")), "and so is the directory holding a file's name");
        Check.True(File.Exists(Path.Combine(ScratchWorkspace.Root, "README (1)")), "and the file holding an extension-less name");

        // A path written as a directory carries a trailing separator, and Path.GetExtension answers "" for
        // it — so without the trim the number lands after that separator and names a directory called " (1)".
        var spelled = Path.Combine(ScratchWorkspace.Root, "pics") + Path.DirectorySeparatorChar;

        Check.Equal(
            Path.Combine(ScratchWorkspace.Root, "pics (1)"),
            RestorePathPlanner.WithFreeNumber(spelled),
            "a path written with a trailing separator numbers the directory it names, not the separator");

        // Two separators, because trimming one and stopping would leave one behind and answer the very same
        // wrong thing. Nothing produces this spelling; a path this library composes never does.
        Check.Equal(
            Path.Combine(ScratchWorkspace.Root, "pics (1)"),
            RestorePathPlanner.WithFreeNumber(spelled + Path.DirectorySeparatorChar),
            "and a path written with two of them is trimmed of both");

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
        Check.Equal("taken (1).bin", Path.GetFileName(renamed.TargetPath), "and it lands under the first free number");
        Check.False(renamed.OccupiedByDirectory, "and what was in the way of the path it asked for is still a file");

        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByFile, isDirectory: false, defaults).Action, "the default file policy skips");
        Check.Equal(RestoreAction.Skip, RestorePathPlanner.Decide(takenByFile, isDirectory: false, new RecycleBinOptions { FileConflict = RestoreFileConflict.Error }).Action, "and refusing is the same action as skipping");

        var renamedDirectory = RestorePathPlanner.Decide(takenByDirectory, isDirectory: true, new RecycleBinOptions { DirectoryConflict = RestoreDirectoryConflict.RenameDirectory });
        Check.Equal(RestoreAction.Rename, renamedDirectory.Action, "renaming a directory takes a free name rather than merging into what is there");
        Check.Equal("taken (1)", Path.GetFileName(renamedDirectory.TargetPath), "and it lands under the first free number");
        Check.True(renamedDirectory.OccupiedByDirectory, "a directory was in the way of the path it asked for");

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
    /// One file comes back: the payload is on the path it was deleted from, byte for byte, and its entry
    /// has left the Recycle Bin.
    /// </summary>
    /// <remarks>
    /// The two halves are separate facts and are asserted separately. The file being back is what a caller
    /// asked for; the count returning to its baseline is what makes the entry gone rather than left, and an
    /// implementation that moved the bytes and kept the record would pass the first assertion and fail the
    /// second. The bytes are read back rather than the length checked, because a payload that arrived
    /// truncated is still a payload that arrived.
    /// <para>
    /// The entry is found by its recorded path, not by its shape in the bin: the bin holds whatever else
    /// this machine has put in it, and only the record says which entry was this case's. Finding it reads
    /// every entry's record, which warms the whole group — a restore that wanted to prove it reads records
    /// on demand would have to keep the group cold, and that is the loader's claim to make rather than this
    /// one's.
    /// </para>
    /// </remarks>
    private static async Task ARecycledFileComesBackToThePathItCameFrom(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        ScratchWorkspace.RequireRecycling("entries to restore");

        var original = workspace.CreateRandomFile(4096);
        var bytes = File.ReadAllBytes(original);

        var baseline = BaselineCount();

        Check.True(
            await RecycleBin.RecycleAsync(original, cancellationToken: context.CancellationToken),
            "the file reached the bin");
        Check.False(File.Exists(original), "and left the disk");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            original);

        var result = await RecycleBin.RestoreAsync([item], cancellationToken: context.CancellationToken);

        Check.True(File.Exists(original), $"the file is back at {original}");
        Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(original)), "byte for byte");
        Check.True(result.Succeeded, $"nothing failed (first: {result.Failures.FirstOrDefault()?.Stage})");
        Check.Equal(1, result.ItemsRestored, "one item restored");
        Check.Equal(4096, result.RestoredSize, "its bytes were measured");
        Check.Equal(0, result.Conflicts.Count, "nothing conflicted");

        // The entry is gone from the bin, which is the other half of "restored".
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "the bin is back to its baseline");

        workspace.Dispose();
    }

    /// <summary>
    /// A path somebody took again while the entry was in the Recycle Bin, under
    /// <see cref="RestoreFileConflict.Skip"/>: nothing on disk is touched, the entry keeps its place in the
    /// bin, and the answer says so without having failed.
    /// </summary>
    /// <remarks>
    /// Three separate facts, asserted separately. The squatter still reading "mine" is that nothing was
    /// touched; the bin holding one more entry than it did is that the entry is still restorable; and
    /// <see cref="RecycleBinRestoreResult.Succeeded"/> staying true is that an item the caller asked to leave
    /// behind is not a failure. Reporting the skip as one would fail the last of those and pass the other
    /// two, which is why it is asserted rather than taken from the empty failures list.
    /// <para>
    /// The record is read to find the entry, so the entry is warm before the restore runs and
    /// <see cref="RecycleBinItem.IsDetailsLoaded"/> would answer the same whether or not it had left the
    /// bin. The count is what says it is still there.
    /// </para>
    /// </remarks>
    private static async Task ATakenFilePathIsSkippedAndTheEntryStaysInTheBin(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var target = workspace.CreateRandomFile(4096);
        var baseline = BaselineCount();

        Check.True(
            await RecycleBin.RecycleAsync(target, cancellationToken: context.CancellationToken),
            "the original reached the bin");

        // At the path the entry wants back, not beside it: a file under some other name is not what a
        // conflict is about.
        File.WriteAllText(target, "mine");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            target);

        var result = await RecycleBin.RestoreAsync(
            [item],
            new RecycleBinOptions { FileConflict = RestoreFileConflict.Skip },
            context.CancellationToken);

        Check.Equal("mine", File.ReadAllText(target), "the file already there is untouched");
        Check.Equal(0, result.ItemsRestored, "nothing was restored");
        Check.Equal(0, result.RestoredPaths.Count, "so nothing is named as restored");
        Check.Equal(0, result.Failures.Count, "a skip is not a failure");
        Check.True(result.Succeeded, "and a conflict alone does not make the result not have succeeded");
        Check.Equal(1, result.Conflicts.Count, "while it is reported");
        Check.Equal(target, result.Conflicts[0].Path, "as the path that was taken");
        Check.False(result.Conflicts[0].OccupiedByDirectory, "by a file");
        Check.Equal(RecycleRestoreConflictAction.Skipped, result.Conflicts[0].Action, "and the action taken");
        Check.Equal(1, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "the entry is still in the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// The same taken path under <see cref="RestoreFileConflict.Replace"/>: the squatter is gone and the
    /// entry leaves the bin, and no conflict is reported because the overwrite answered itself.
    /// </summary>
    /// <remarks>
    /// The bytes read back are the original's and not an absence: a squatter removed and nothing put in its
    /// place would leave a missing file rather than a restored one, and the length is not what tells them
    /// apart.
    /// <para>
    /// <see cref="RecycleBinRestoreResult.Conflicts"/> is empty, which is the same rule the design gives a
    /// directory merge under <see cref="RestoreDirectoryConflict.MergeReplace"/> and the reason there is no
    /// enumerator for a conflict an overwrite resolved. A report here would tell the caller about something
    /// that no longer exists.
    /// </para>
    /// </remarks>
    private static async Task ATakenFilePathIsReplacedAndTheEntryLeavesTheBin(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var target = workspace.CreateRandomFile(4096);
        var bytes = File.ReadAllBytes(target);
        var baseline = BaselineCount();

        Check.True(
            await RecycleBin.RecycleAsync(target, cancellationToken: context.CancellationToken),
            "the original reached the bin");

        File.WriteAllText(target, "mine");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            target);

        var result = await RecycleBin.RestoreAsync(
            [item],
            new RecycleBinOptions { FileConflict = RestoreFileConflict.Replace },
            context.CancellationToken);

        Check.True(result.Succeeded, $"nothing failed (first: {result.Failures.FirstOrDefault()?.Stage})");
        Check.Equal(1, result.ItemsRestored, "the entry was restored");
        Check.Equal(1, result.RestoredPaths.Count, "and it is named");
        Check.Equal(target, result.RestoredPaths[0], "at the path it was deleted from, occupied or not");
        Check.Equal(4096, result.RestoredSize, "and its bytes were measured");
        Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(target)), "the squatter's content is gone and the original's is here");
        Check.Equal(0, result.Failures.Count, "an overwrite is not a failure");
        Check.Equal(0, result.Conflicts.Count, "and is not a conflict to report either");
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "the entry has left the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// The same taken path under <see cref="RestoreFileConflict.Rename"/>: the entry comes back beside what
    /// was in the way, the file that was already there is not the thing that moved, and the conflict names
    /// where the entry landed.
    /// </summary>
    /// <remarks>
    /// The free name is worked out before the restore rather than read back out of the answer, because the
    /// first call is the one that is free: once the payload is lying there, the same call counts on to the
    /// next number.
    /// <para>
    /// Both halves of "the squatter was not the thing that was renamed" are asserted. Its content still
    /// reads "mine" is that it was left alone, and the original's bytes are at the free name is that the
    /// entry is what came back. Reading the bytes back rather than the length is the difference between a
    /// payload that arrived and one that arrived intact.
    /// </para>
    /// <para>
    /// The conflict's path is the one the entry landed at rather than the one it collided with, which is what
    /// <see cref="RecycleRestoreConflictAction.RestoredRenamed"/> reports and the reason a caller reading
    /// <see cref="RecycleBinRestoreResult.RestoredPaths"/> finds the same path in both lists.
    /// </para>
    /// </remarks>
    private static async Task ATakenFilePathIsRestoredUnderAFreeName(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var target = workspace.CreateRandomFile(4096);
        var bytes = File.ReadAllBytes(target);
        var baseline = BaselineCount();

        Check.True(
            await RecycleBin.RecycleAsync(target, cancellationToken: context.CancellationToken),
            "the original reached the bin");

        File.WriteAllText(target, "mine");

        // Taken before the restore: the name is free while the squatter holds the one it numbers, and holding
        // the answer of the second call would name a second file rather than the one that was set down.
        var renamed = RestorePathPlanner.WithFreeNumber(target);

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            target);

        var result = await RecycleBin.RestoreAsync(
            [item],
            new RecycleBinOptions { FileConflict = RestoreFileConflict.Rename },
            context.CancellationToken);

        Check.True(result.Succeeded, $"nothing failed (first: {result.Failures.FirstOrDefault()?.Stage})");
        Check.Equal(1, result.ItemsRestored, "the entry was restored");
        Check.Equal(1, result.RestoredPaths.Count, "and it is named");
        Check.Equal(renamed, result.RestoredPaths[0], "under the first free name beside the one it wanted");
        Check.True(File.Exists(renamed), $"the payload is at {renamed}");
        Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(renamed)), "byte for byte");
        Check.Equal("mine", File.ReadAllText(target), "and the file already there is untouched");
        Check.Equal(0, result.Failures.Count, "a rename resolved itself, so nothing failed");

        Check.Equal(1, result.Conflicts.Count, "and it is reported");
        Check.Equal(renamed, result.Conflicts[0].Path, "as the path the entry landed at");
        Check.False(result.Conflicts[0].OccupiedByDirectory, "with what was in the way reported as a file");
        Check.Equal(RecycleRestoreConflictAction.RestoredRenamed, result.Conflicts[0].Action, "and the action taken");
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "the entry has left the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// The same taken path under <see cref="RestoreFileConflict.Error"/>: nothing is touched, and the refusal
    /// is reported as a conflict and as a failure, which is what makes the result not have succeeded.
    /// </summary>
    /// <remarks>
    /// Both lists carry it because they answer different questions — what was in the way, and what did not
    /// happen — and a caller reading only <see cref="RecycleBinRestoreResult.Conflicts"/> could not tell this
    /// from a skip, which is the one thing the two policies are apart.
    /// <para>
    /// The code and the round count are zero because nothing was attempted: the refusal is settled before any
    /// move, which is what <see cref="RecycleFailure.Attempts"/> reports as zero everywhere else too.
    /// </para>
    /// </remarks>
    private static async Task ATakenFilePathIsRefusedAsBothAConflictAndAFailure(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var target = workspace.CreateRandomFile(4096);
        var baseline = BaselineCount();

        Check.True(
            await RecycleBin.RecycleAsync(target, cancellationToken: context.CancellationToken),
            "the original reached the bin");

        File.WriteAllText(target, "mine");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            target);

        var result = await RecycleBin.RestoreAsync(
            [item],
            new RecycleBinOptions { FileConflict = RestoreFileConflict.Error },
            context.CancellationToken);

        Check.False(result.Succeeded, "a refused conflict is what makes the result not have succeeded");
        Check.Equal("mine", File.ReadAllText(target), "the file already there is untouched");
        Check.Equal(0, result.ItemsRestored, "nothing was restored");
        Check.Equal(0, result.RestoredPaths.Count, "so nothing is named as restored");

        Check.Equal(1, result.Conflicts.Count, "and it is a conflict like any other");
        Check.Equal(target, result.Conflicts[0].Path, "as the path that was taken");
        Check.False(result.Conflicts[0].OccupiedByDirectory, "by a file");
        Check.Equal(RecycleRestoreConflictAction.Skipped, result.Conflicts[0].Action, "and the action taken, which is the same one a skip takes");

        Check.Equal(1, result.Failures.Count, "and it is a failure as well");
        Check.Equal(target, result.Failures[0].Path, "under the path that was refused");
        Check.Equal(RecycleFailureStage.RestoreConflictRefused, result.Failures[0].Stage, "at the stage a refusal is reported at");
        Check.Equal(0, result.Failures[0].HResult, "nothing was attempted, so the file system has no code to report");
        Check.Equal(0, result.Failures[0].Attempts, "and nothing was tried");
        Check.Equal(1, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "the entry stays in the bin, so the refusal is worth a second attempt");

        workspace.Dispose();
    }

    /// <summary>
    /// A directory comes back as it was: every file in it at every depth, with its bytes, and its entry
    /// gone from the bin.
    /// </summary>
    /// <remarks>
    /// The tree is three levels deep because a whole-directory move and a walk that drops the deepest
    /// level are the same code at one level, and only the file at the bottom tells them apart. The files
    /// hold random bytes rather than a word the case could compare by name, because a payload that
    /// arrived empty, truncated, or holding a neighbour's content is a payload that arrived and not a
    /// tree that came back.
    /// <para>
    /// The empty directory is asserted as well, and it is the only assertion that is about the directories
    /// rather than the files in them: "whole" is the claim, and a tree that came back without it lost a
    /// directory that was in it.
    /// </para>
    /// <para>
    /// The entry leaving the bin is the other half of "came back", and it is counted against a baseline
    /// for the reason <see cref="BaselineCount"/> gives. <see cref="RecycleBinRestoreResult.Succeeded"/>
    /// and <see cref="RecycleBinRestoreResult.ItemsRestored"/> are read as well, so an implementation that
    /// reported the move and left the record behind would fail this rather than pass two of its assertions.
    /// </para>
    /// </remarks>
    private static async Task ADirectoryComesBackWithItsTree(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var root = Path.Combine(ScratchWorkspace.Root, "tree");

        Directory.CreateDirectory(Path.Combine(root, "nested", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));

        var top = RandomBytes(4096);
        var middle = RandomBytes(8192);
        var bottom = RandomBytes(1024);

        File.WriteAllBytes(Path.Combine(root, "top.bin"), top);
        File.WriteAllBytes(Path.Combine(root, "nested", "middle.bin"), middle);
        File.WriteAllBytes(Path.Combine(root, "nested", "deep", "bottom.bin"), bottom);

        var baseline = BaselineCount();

        Check.True(await RecycleBin.RecycleAsync(root, cancellationToken: context.CancellationToken), "the tree reached the bin");
        Check.False(Directory.Exists(root), "and left the disk");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            root);

        Check.True(item.IsDirectory, "and the record says it was a directory");

        var result = await RecycleBin.RestoreAsync([item], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"nothing failed (first: {result.Failures.FirstOrDefault()?.Stage})");
        Check.Equal(0, result.Failures.Count, "so nothing reached the failures list");
        Check.Equal(1, result.ItemsRestored, "the directory was restored");
        Check.Equal(1, result.RestoredPaths.Count, "and is named");
        Check.Equal(root, result.RestoredPaths[0], "at the path it was deleted from");
        Check.Equal(0, result.Conflicts.Count, "a free destination conflicts with nothing");

        Check.True(Directory.Exists(root), $"the directory is back at {root}");
        Check.True(top.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "top.bin"))), "its top file is back byte for byte");
        Check.True(middle.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "nested", "middle.bin"))), "the one inside it is back byte for byte");
        Check.True(bottom.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "nested", "deep", "bottom.bin"))), "and so is the deepest one");
        Check.True(Directory.Exists(Path.Combine(root, "empty")), "and so is the directory that had nothing in it");

        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "and the entry has left the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// A record names the path a thing was deleted from, and the folders on the way to it can be gone by
    /// the time anyone restores: the tree lands under parents this call creates.
    /// </summary>
    /// <remarks>
    /// Two of them are taken away rather than one, because <see cref="Directory.CreateDirectory(string)"/>
    /// creating a whole chain and creating one level of it are the same call and the case can only tell
    /// them apart if more than one level was missing. The original's own directory goes with them, so
    /// nothing under the path the record names is there and the move has to make every level itself.
    /// <para>
    /// What is asserted about the tree is what the first case asserts, and for the same reason: the
    /// deepest file is what says the tree arrived rather than the top of it being written out again, and
    /// the count is what says the entry left the bin rather than the payload having been copied.
    /// </para>
    /// </remarks>
    private static async Task ADirectoryComesBackUnderParentsThatAreGone(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var gone = Path.Combine(ScratchWorkspace.Root, "gone");
        var root = Path.Combine(gone, "deeper", "tree");

        Directory.CreateDirectory(Path.Combine(root, "nested"));
        var bytes = RandomBytes(4096);
        File.WriteAllBytes(Path.Combine(root, "nested", "leaf.bin"), bytes);

        var baseline = BaselineCount();

        Check.True(await RecycleBin.RecycleAsync(root, cancellationToken: context.CancellationToken), "the tree reached the bin");

        Directory.Delete(gone, recursive: true);
        Check.False(Directory.Exists(gone), "and every folder on the way to it was taken away afterwards");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            root);

        var result = await RecycleBin.RestoreAsync([item], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"nothing failed (first: {result.Failures.FirstOrDefault()?.Stage})");
        Check.Equal(1, result.ItemsRestored, "the directory was restored");
        Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "nested", "leaf.bin"))), "the leaf came back, under folders this call made");
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "and the entry has left the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// The file half of the same claim: a record names the path a thing was deleted from, and a file
    /// whose folder has since been emptied by hand lands under a parent this call creates.
    /// </summary>
    /// <remarks>
    /// A file is the shape that needs this case of its own. A directory case cannot stand in for one:
    /// both go through the same private method, and moving that call inside a branch that only a
    /// directory takes would leave every case written about directories green while files quietly went
    /// back to needing a folder someone else makes. The one ordinary file case in the suite restores
    /// into the scratch root, which is always there, so nothing else in the file held it up either.
    /// <para>
    /// The folder is deleted with <see cref="Directory.Delete(string, bool)"/> rather than recursively,
    /// because by then the file is in the bin and what is left in the folder is nothing — a recursive
    /// delete would be the same call done more carefully than it needs to be. The assertion right after
    /// it is what keeps the case from passing for the wrong reason: without it, a folder that was never
    /// created would be restored into without a parent ever being asked for.
    /// </para>
    /// <para>
    /// The bytes and the count are the two halves of "came back", for the reason the cases above give.
    /// </para>
    /// </remarks>
    private static async Task AFileComesBackUnderAParentThatIsGone(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("entries to restore");

        var folder = Path.Combine(ScratchWorkspace.Root, "taken-away");
        Directory.CreateDirectory(folder);

        var original = Path.Combine(folder, "leaf.bin");
        var bytes = RandomBytes(4096);
        File.WriteAllBytes(original, bytes);

        var baseline = BaselineCount();

        Check.True(await RecycleBin.RecycleAsync(original, cancellationToken: context.CancellationToken), "the file reached the bin");

        Directory.Delete(folder);
        Check.False(Directory.Exists(folder), "and the folder it was in was taken away afterwards");

        var item = FindTheEntryFor(
            await RecycleBin.GetItemsAsync(ScratchWorkspace.VolumeRoot, cancellationToken: context.CancellationToken),
            original);

        var result = await RecycleBin.RestoreAsync([item], cancellationToken: context.CancellationToken);

        Check.True(result.Succeeded, $"nothing failed (first: {result.Failures.FirstOrDefault()?.Stage})");
        Check.Equal(1, result.ItemsRestored, "the file was restored");
        Check.True(File.Exists(original), $"and it is back at {original}");
        Check.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(original)), "byte for byte");
        Check.Equal(0, RecycleBinProbe.QueryItemCount(ScratchWorkspace.VolumeRoot) - baseline, "and the entry has left the bin");

        workspace.Dispose();
    }

    /// <summary>
    /// A buffer of <paramref name="sizeBytes"/> random bytes.
    /// </summary>
    /// <remarks>
    /// For a case that has to say what came back is what went in. Bytes that are not there beforehand are
    /// what make that a real comparison rather than a tautology: a payload that arrived empty, truncated,
    /// or holding a neighbour's content is not distinguishable from one that arrived whole when both were
    /// written as the same word.
    /// </remarks>
    private static byte[] RandomBytes(int sizeBytes)
    {
        var bytes = new byte[sizeBytes];
        Random.Shared.NextBytes(bytes);

        return bytes;
    }

    /// <summary>
    /// The one entry of a listing that was deleted from <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// How every case that restores something finds its subject. A bin on a working machine holds
    /// everything else the owner has deleted, so an entry cannot be told apart by its name or by its
    /// position in the walk; the record it carries is the only thing that says which entry a case made.
    /// Reading it is also what warms the entry, so a case that filters this way gets a warm group back and
    /// cannot claim anything about how the details were read.
    /// </remarks>
    /// <exception cref="CheckFailedException">
    /// No entry, or more than one, names <paramref name="path"/>. Either way there is no single subject for
    /// the case, and picking one of them would make the assertions below about an entry nobody chose.
    /// </exception>
    private static RecycleBinItem FindTheEntryFor(RecycleBinContents listing, string path)
    {
        var named = Listing
            .OnlyEntries(listing, ScratchWorkspace.VolumeRoot)
            .Where(entry => string.Equals(entry.OriginalPath, path, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Check.Equal(1, named.Length, $"exactly one entry in the bin was deleted from {path}");

        return named[0];
    }

    /// <summary>
    /// How many entries the run's volume held before the case put anything there.
    /// </summary>
    /// <remarks>
    /// The bin is shared with everything else on the machine, so what a case can assert is a difference and
    /// not an absolute: "the bin holds one entry" is false of any real desktop. Taken after
    /// <c>Recreate</c> and before anything is recycled, which is the only point where the count is the
    /// machine's rather than the case's.
    /// <para>
    /// Read through <see cref="RecycleBinProbe.MeasureOrFail"/> rather than as a bare count because this
    /// number is subtracted from a later one: a volume that could not be read arrives as <c>-1</c>, and
    /// <c>-1 - (-1)</c> is zero, so an unreadable bin would satisfy "back to its baseline" having measured
    /// nothing at all.
    /// </para>
    /// </remarks>
    private static long BaselineCount()
    {
        var (before, _) = RecycleBinProbe.MeasureOrFail(ScratchWorkspace.VolumeRoot, "the baseline count");

        return before;
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

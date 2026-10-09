using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// What the suite writes to, and how it comes to own it.
/// </summary>
/// <remarks>
/// Every case in this repository writes files and deletes directories, and this one writes nothing
/// outside the directory the run claimed under the volume root <c>--scratch</c> named. That rule and the
/// half of this file that tests the claim mechanism are in tension: a claim is by definition a direct
/// child of a volume root, so proving that one is made means making one in the root.
/// <para>
/// The way out is already in the harness. <c>subst</c> maps a drive letter onto this run's own directory,
/// and a substituted drive is a volume root to everything above it — <c>Path.GetPathRoot</c> answers it
/// and nothing rejects it — while everything written through it lands inside the directory the run already
/// owns. So the ten cases below that claim work through a substituted root and write nothing beside it.
/// <para>
/// The one other thing that appears in a volume root belongs to the library rather than to any case:
/// <c>LongPathStager</c> creates its <c>$RCW-</c> directories there, because a staging path has to be
/// short and the volume root is the shortest place on the right volume.
/// </para>
/// <para>
/// A name written into the source is not something a case may take ownership of either.
/// <c>Directory.CreateDirectory</c> answers an existing directory with success, so a name from this file
/// would be adopted and then removed with a recursive delete. The bystanders the cases need are claims of
/// their own, and the one name a case has to invent is a Guid, which is the same check
/// <see cref="ScratchDirectory"/> makes before it takes a name.
/// </para>
/// </remarks>
internal static class ScratchSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ScratchSuite." + nameof(ClaimCreatesADirectoryInsideTheVolumeRoot), ClaimCreatesADirectoryInsideTheVolumeRoot),
        new TestCase("ScratchSuite." + nameof(ClaimNeverReusesADirectoryThatAlreadyExists), ClaimNeverReusesADirectoryThatAlreadyExists),
        new TestCase("ScratchSuite." + nameof(ClaimRegeneratesUntilItFindsAFreeName), ClaimRegeneratesUntilItFindsAFreeName),
        new TestCase("ScratchSuite." + nameof(ClaimGivesUpRatherThanSpin), ClaimGivesUpRatherThanSpin),
        new TestCase("ScratchSuite." + nameof(ClaimNamesADirectoryTheCallerCannotHaveWrittenDown), ClaimNamesADirectoryTheCallerCannotHaveWrittenDown),
        new TestCase("ScratchSuite." + nameof(TwoClaimsAreTwoDirectories), TwoClaimsAreTwoDirectories),
        new TestCase("ScratchSuite." + nameof(ASubdirectoryLivesUnderTheClaimedDirectory), ASubdirectoryLivesUnderTheClaimedDirectory),
        new TestCase("ScratchSuite." + nameof(RemoveDeletesTheClaimedDirectoryAndNothingElse), RemoveDeletesTheClaimedDirectoryAndNothingElse),
        new TestCase("ScratchSuite." + nameof(RemoveIsSafeWhenTheDirectoryIsAlreadyGone), RemoveIsSafeWhenTheDirectoryIsAlreadyGone),
        new TestCase("ScratchSuite." + nameof(ClaimRefusesAParentThatIsNotAVolumeRoot), ClaimRefusesAParentThatIsNotAVolumeRoot),
        new TestCase("ScratchSuite." + nameof(AVolumeRootIsItsOwnNormalizedSpelling), AVolumeRootIsItsOwnNormalizedSpelling),
        new TestCase("ScratchSuite." + nameof(ADirectoryUnderAVolumeRootIsNotAVolumeRoot), ADirectoryUnderAVolumeRootIsNotAVolumeRoot),
        new TestCase("ScratchSuite." + nameof(ADriveLetterIsNotAVolumeRoot), ADriveLetterIsNotAVolumeRoot),
        new TestCase("ScratchSuite." + nameof(AShareIsWrittenWithBackslashes), AShareIsWrittenWithBackslashes),
        new TestCase("ScratchSuite." + nameof(RequireRefusesALetterNothingIsMountedOn), RequireRefusesALetterNothingIsMountedOn),
        new TestCase("ScratchSuite." + nameof(AnUnmountedLetterIsFoundRatherThanWrittenDown), AnUnmountedLetterIsFoundRatherThanWrittenDown),
        new TestCase("ScratchSuite." + nameof(AMountedLetterIsSkippedRatherThanActedOn), AMountedLetterIsSkippedRatherThanActedOn),
    ];

    /// <summary>
    /// Runs a body against a volume root of its own that is physically this run's directory, so a claim can
    /// be tested for what it claims to be — a direct child of a volume root — without anything being
    /// created in the volume root the caller named.
    /// </summary>
    /// <remarks>
    /// The substituted root is <see cref="ScratchWorkspace.RunRoot"/> rather than
    /// <see cref="ScratchWorkspace.Root"/>, because it needs nothing of the volume but its existence: a
    /// claim is created and removed here, and a volume with no Recycle Bin would say nothing about that.
    /// </remarks>
    private static async Task OnASubstitutedVolumeRoot(TestContext context, Func<char, Task> body)
    {
        var letter = SubstitutedVolume.FirstUnusedDriveLetter();

        try
        {
            await SubstitutedVolume
                .Substitute(letter, ScratchWorkspace.RunRoot, context.CancellationToken)
                .ConfigureAwait(false);

            await body(letter).ConfigureAwait(false);
        }
        finally
        {
            await SubstitutedVolume.RemoveSubstitute(letter).ConfigureAwait(false);
        }
    }

    private static string SubstitutedRoot(char letter) =>
        letter + ":" + Path.DirectorySeparatorChar;

    /// <summary>
    /// The name a claim took, so it can be offered to a later claim as one that is already there.
    /// </summary>
    private static string NameOf(ScratchDirectory claim) =>
        Path.GetFileName(Normalize(claim.FullPath));

    /// <summary>
    /// A claimed directory is a direct child of the volume root the caller named, so nothing about it
    /// depends on where the repository happens to sit.
    /// </summary>
    private static Task ClaimCreatesADirectoryInsideTheVolumeRoot(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var volumeRoot = SubstitutedRoot(letter);
            var claimed = ScratchDirectory.Claim(volumeRoot);

            try
            {
                Check.True(Directory.Exists(claimed.FullPath), $"the claimed directory exists: {claimed.FullPath}");
                Check.Equal(Trimmed(ParentOf(claimed.FullPath)), Trimmed(volumeRoot), "and it is a direct child of the root that was named");
                Check.Equal(volumeRoot, claimed.Volume, "which the claim also remembers as the volume it works on");
            }
            finally
            {
                claimed.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// The case the whole mechanism exists for. <c>Directory.CreateDirectory</c> reports success for a
    /// directory that was already there, so a claim that did not check first would hand a case somebody
    /// else's directory — and the claim's own removal would then delete it.
    /// </summary>
    private static Task ClaimNeverReusesADirectoryThatAlreadyExists(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var volumeRoot = SubstitutedRoot(letter);

            // Taken, because this run claimed it first, and holding a file makes it worth taking rather
            // than merely present.
            var bystander = ScratchDirectory.Claim(volumeRoot);
            var notOurs = Path.Combine(bystander.FullPath, "not-ours.txt");
            File.WriteAllText(notOurs, "written by this test");

            // Offered the taken name first and a name nothing holds second, so the claim has to move past
            // the one it was handed rather than retry it. A seam that only ever offers the taken name would
            // instead be the give-up case, which is what ClaimGivesUpRatherThanSpin asserts.
            var free = Guid.NewGuid().ToString("N");
            var offered = new Queue<string>([NameOf(bystander), free]);

            ScratchDirectory? claimed = null;

            try
            {
                claimed = ScratchDirectory.Claim(volumeRoot, () => offered.Dequeue());

                Check.Equal(
                    0,
                    offered.Count,
                    "the taken name was offered and the free name was taken in turn, rather than the taken one offered again");
                Check.Equal(
                    Normalize(Path.Combine(volumeRoot, free)),
                    Normalize(claimed.FullPath),
                    "a name that was already taken is not adopted, and the next one is what the claim takes");
                Check.True(File.Exists(notOurs), "and the directory that was there keeps its file");
            }
            finally
            {
                claimed?.Remove();
                bystander.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// One taken name is a retry; a run of them is a loop, and the claim has to come back with a directory
    /// rather than with the last name it was offered.
    /// </summary>
    private static Task ClaimRegeneratesUntilItFindsAFreeName(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var volumeRoot = SubstitutedRoot(letter);
            var taken = new[] { ScratchDirectory.Claim(volumeRoot), ScratchDirectory.Claim(volumeRoot) };

            var free = Guid.NewGuid().ToString("N");
            var offered = new Queue<string>([.. taken.Select(NameOf), free]);

            ScratchDirectory? claimed = null;

            try
            {
                claimed = ScratchDirectory.Claim(volumeRoot, () => offered.Dequeue());

                Check.Equal(
                    Normalize(Path.Combine(volumeRoot, free)),
                    Normalize(claimed.FullPath),
                    "the claim settles for the first name that was free, not the last one offered");
                Check.Equal(0, offered.Count, "and it stops asking once it has one");
            }
            finally
            {
                claimed?.Remove();

                foreach (var claim in taken)
                {
                    claim.Remove();
                }
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// A machine where every name is taken would otherwise spin here for the rest of the run and report
    /// nothing. Naming the budget is what turns that into a failure that says what happened.
    /// </summary>
    private static Task ClaimGivesUpRatherThanSpin(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var taken = ScratchDirectory.Claim(SubstitutedRoot(letter));
            var name = NameOf(taken);

            try
            {
                var failure = Check.Throws<InvalidOperationException>(
                    () => ScratchDirectory.Claim(taken.Volume, () => name),
                    "a name that is never free is a failure, not an endless loop");

                Check.True(failure.Message.Contains(name, StringComparison.Ordinal), $"and it names the name that was taken ({failure.Message})");
            }
            finally
            {
                taken.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// Nothing in the source names the directory, which is the property that makes the claim safe: a name
    /// written into a test is a name the suite could be pointed at twice.
    /// </summary>
    private static Task ClaimNamesADirectoryTheCallerCannotHaveWrittenDown(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var claimed = ScratchDirectory.Claim(SubstitutedRoot(letter));

            try
            {
                var name = NameOf(claimed);

                Check.NotEqual("Test", name, "the name is not a fixed one this suite hard-codes");
                Check.NotEqual("scratch", name, "nor a purpose spelled out as a name of its own");
                Check.True(name.Length >= 32, $"the name is long enough to be a generated one ({name.Length} chars)");
                Check.Equal(32, name.Length, "and it is exactly as long as a Guid has characters to spell");
                Check.True(Guid.TryParse(name, out _), $"and it is the Guid it claims to be ({name})");
            }
            finally
            {
                claimed.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>Two claims are two directories, so a second case cannot land in the first one's tree.</summary>
    private static Task TwoClaimsAreTwoDirectories(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var volumeRoot = SubstitutedRoot(letter);
            var first = ScratchDirectory.Claim(volumeRoot);
            var second = ScratchDirectory.Claim(volumeRoot);

            try
            {
                Check.NotEqual(Normalize(first.FullPath), Normalize(second.FullPath), "two claims of the same root are two directories");
            }
            finally
            {
                first.Remove();
                second.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>Consumers share one claim and take a named child of it.</summary>
    private static Task ASubdirectoryLivesUnderTheClaimedDirectory(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var claimed = ScratchDirectory.Claim(SubstitutedRoot(letter));

            try
            {
                var child = claimed.Subdirectory("long");

                Check.Equal(Normalize(child), Normalize(Path.Combine(claimed.FullPath, "long")), "the child is the named directory under the claim");
                Check.True(Directory.Exists(child), "and it exists");
            }
            finally
            {
                claimed.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// Removing a claim is a recursive delete, so it must delete the path the claim returned and nothing
    /// on the volume beside it.
    /// </summary>
    private static Task RemoveDeletesTheClaimedDirectoryAndNothingElse(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var volumeRoot = SubstitutedRoot(letter);

            // A sibling, and a claim like any other: the point is that the path beside the claim survives,
            // and a bystander this run does not own would not be a thing the suite is allowed to make.
            var bystander = ScratchDirectory.Claim(volumeRoot);
            var notOurs = Path.Combine(bystander.FullPath, "not-ours.txt");
            File.WriteAllText(notOurs, "written by this test");

            var claimed = ScratchDirectory.Claim(volumeRoot);

            try
            {
                File.WriteAllText(Path.Combine(claimed.FullPath, "ours.txt"), "scratch");

                claimed.Remove();

                Check.False(Directory.Exists(Normalize(claimed.FullPath)), "the claimed directory is gone");
                Check.True(File.Exists(notOurs), "and the directory beside it is untouched");
            }
            finally
            {
                claimed.Remove();
                bystander.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>
    /// Cleanup runs in a <c>finally</c>, sometimes after the case already failed, and twice is what two
    /// cleanup paths mean. Failing on the second would replace the case's own failure with a complaint
    /// about its own cleanup.
    /// </summary>
    private static Task RemoveIsSafeWhenTheDirectoryIsAlreadyGone(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            var claimed = ScratchDirectory.Claim(SubstitutedRoot(letter));

            claimed.Remove();
            claimed.Remove();

            Check.False(Directory.Exists(Normalize(claimed.FullPath)), "removing twice is not a failure");

            return Task.CompletedTask;
        });

    /// <summary>
    /// A claim is a direct child of a volume root. Handed a directory instead, it would create the claim
    /// inside the caller's data — and its removal would then take that data's parent with it.
    /// </summary>
    private static Task ClaimRefusesAParentThatIsNotAVolumeRoot(TestContext context) =>
        OnASubstitutedVolumeRoot(context, letter =>
        {
            // A directory under a claim rather than under the volume root: it is still a path inside a
            // volume, which is what the claim has to refuse, and creating it costs nothing this run does not
            // already own.
            var claimed = ScratchDirectory.Claim(SubstitutedRoot(letter));
            var parent = claimed.Subdirectory("parent");

            try
            {
                var failure = Check.Throws<ArgumentException>(
                    () => ScratchDirectory.Claim(parent),
                    "a directory is not a volume root, so nothing is created inside it");

                Check.True(failure.Message.Contains("volume root", StringComparison.OrdinalIgnoreCase), $"and says why ({failure.Message})");
                Check.Equal(
                    0,
                    Directory.GetDirectories(parent).Length,
                    "and the caller's directory still holds nothing this suite put there");
            }
            finally
            {
                claimed.Remove();
            }

            return Task.CompletedTask;
        });

    /// <summary>Nothing to normalize: the root is already spelled with a separator and nothing else.</summary>
    private static Task AVolumeRootIsItsOwnNormalizedSpelling(TestContext context)
    {
        var volumeRoot = ScratchWorkspace.VolumeRoot;

        Check.True(volumeRoot.EndsWith(Path.DirectorySeparatorChar), $"the scratch volume root has a trailing separator: {volumeRoot}");
        Check.Equal(volumeRoot, VolumeRoot.Normalize(volumeRoot), "and normalizing it changes nothing");

        return Task.CompletedTask;
    }

    private static Task ADirectoryUnderAVolumeRootIsNotAVolumeRoot(TestContext context)
    {
        var directory = Path.Combine(ScratchWorkspace.VolumeRoot, "ScratchSuite-a-directory");

        var failure = Check.Throws<ArgumentException>(
            () => VolumeRoot.Normalize(directory),
            "a directory under a root is a path inside a volume, not a volume");

        Check.True(failure.Message.Contains(directory, StringComparison.Ordinal), $"and it names the path it refused ({failure.Message})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>Path.GetPathRoot</c> answers <c>X:</c> for a bare letter, and that is drive-relative: what it
    /// names is whichever directory the process happens to have current for that drive.
    /// </summary>
    private static Task ADriveLetterIsNotAVolumeRoot(TestContext context)
    {
        var letter = AbsentVolume.Letter();

        Check.Throws<ArgumentException>(
            () => VolumeRoot.Normalize($"{letter}:"),
            "a bare drive letter is not a root, because it is resolved against the current directory");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The forward-slash spelling exists because not every command line can carry two leading
    /// backslashes: one that collapses them delivers a single leading separator, which resolves against the
    /// current drive instead of naming a share.
    /// </summary>
    private static Task AShareIsWrittenWithBackslashes(TestContext context)
    {
        var normalized = VolumeRoot.Normalize("//ScratchSuite-server/share/");

        Check.Equal(@"\\ScratchSuite-server\share\", normalized, "the forward-slash form is read as the share it names");
        Check.Equal(
            @"\\ScratchSuite-server\share\",
            VolumeRoot.Normalize(@"\\ScratchSuite-server\share\"),
            "and the spelling Windows writes normalizes to itself");

        return Task.CompletedTask;
    }

    /// <summary>A letter nothing is mounted on names no volume, and claiming under it has to say so.</summary>
    private static Task RequireRefusesALetterNothingIsMountedOn(TestContext context)
    {
        var letter = AbsentVolume.Letter();
        var root = AbsentVolume.RootOf(letter);

        var failure = Check.Throws<InvalidOperationException>(
            () => VolumeRoot.Require(root, "--scratch"),
            "a root the machine does not have is not somewhere to write");

        Check.True(failure.Message.Contains(root, StringComparison.Ordinal), $"and it names the root that was refused ({failure.Message})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The letter is asked of the machine rather than written down, so a machine that has the
    /// written-down letter mounted cannot be handed a stranger's volume to empty.
    /// </summary>
    /// <remarks>
    /// Neither assertion can tell a looked-up letter from a written-down one — <c>'Z'</c> comes out of
    /// the lookup whenever <c>Z:</c> is genuinely free, and that is the lookup working rather than the
    /// source deciding. What is checkable is the property it guarantees, and both halves of it are
    /// checked here: a letter that <see cref="DriveInfo"/> does not report and whose root does not
    /// resolve.
    /// </remarks>
    private static Task AnUnmountedLetterIsFoundRatherThanWrittenDown(TestContext context)
    {
        var letter = AbsentVolume.Letter();

        Check.False(
            DriveInfo.GetDrives().Any(drive => char.ToUpperInvariant(drive.Name[0]) == letter),
            $"the letter {letter}: is not among the drives this machine reports");

        Check.False(
            Directory.Exists(AbsentVolume.RootOf(letter)),
            $"and {AbsentVolume.RootOf(letter)} does not resolve, which is the second thing the lookup checks before handing a letter out");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A letter that is mounted ends the case as not run, rather than being acted on. Nothing else
    /// stands between a run and a Recycle Bin that is not the caller's: the letter can be taken by another
    /// process between the lookup and the call, and this is what notices.
    /// </summary>
    /// <remarks>
    /// The guard is asserted to <em>skip</em> rather than merely to return, because a case that returns
    /// normally is counted as passed and the guard would then be reporting coverage it did not produce —
    /// and the call behind it is the one that empties a volume.
    /// </remarks>
    private static Task AMountedLetterIsSkippedRatherThanActedOn(TestContext context)
    {
        var letter = char.ToUpperInvariant(DriveInfo.GetDrives().First().Name[0]);

        var skip = Check.Throws<CheckSkippedException>(
            () => AbsentVolume.RequireAbsent(letter, "emptying a volume that is not there"),
            "a letter that is mounted ends the case as not run rather than acting on that volume");

        Check.True(skip.Message.Contains(letter), $"and the skip names the letter it refused ({skip.Message})");

        return Task.CompletedTask;
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The directory a path sits in, as the filesystem spells it: a drive root keeps its separator, a
    /// directory on a share does not.
    /// </summary>
    private static string ParentOf(string path) => Directory.GetParent(Normalize(path))?.FullName ?? Normalize(path);

    /// <summary>
    /// A path without its trailing separator, so two spellings of one directory compare equal.
    /// <c>Directory.GetParent</c> is not symmetric about the separator at a volume root: asked for the
    /// parent of <c>D:\</c> it answers <c>D:</c>, and asked for the parent of a directory on a share it
    /// answers without one.
    /// </summary>
    private static string Trimmed(string path) => Normalize(path);
}
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// What the suite writes to, and how it comes to own it.
/// </summary>
/// <remarks>
/// Every case in this repository writes files and deletes directories, and this one writes nothing outside
/// the directory the run claimed under the volume root <c>--scratch</c> named.
/// <list type="bullet">
/// <item>There is no tension with testing the claim mechanism, because a claim no longer has to be a direct
/// child of a volume root. <see cref="ScratchDirectory"/> has two entry points: one claims the run's
/// directory under a volume root, the other refuses any parent not already inside it. So the cases below
/// claim under <see cref="ScratchWorkspace.Root"/> and each claim is a real one in the real place.</item>
/// <item>A name written into the source is not something a case may take ownership of either.
/// <c>Directory.CreateDirectory</c> answers an existing directory with success, so a name from this file
/// would be adopted and then removed with a recursive delete. The one name a case has to invent is a Guid,
/// which is the same check <see cref="ScratchDirectory"/> makes before it takes a name.</item>
/// </list>
/// </remarks>
internal static class ScratchSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("ScratchSuite." + nameof(ClaimCreatesADirectoryUnderTheParentItWasGiven), ClaimCreatesADirectoryUnderTheParentItWasGiven),
        new TestCase("ScratchSuite." + nameof(ClaimNeverReusesADirectoryThatAlreadyExists), ClaimNeverReusesADirectoryThatAlreadyExists),
        new TestCase("ScratchSuite." + nameof(ClaimRegeneratesUntilItFindsAFreeName), ClaimRegeneratesUntilItFindsAFreeName),
        new TestCase("ScratchSuite." + nameof(ClaimGivesUpRatherThanSpin), ClaimGivesUpRatherThanSpin),
        new TestCase("ScratchSuite." + nameof(ClaimNamesADirectoryTheCallerCannotHaveWrittenDown), ClaimNamesADirectoryTheCallerCannotHaveWrittenDown),
        new TestCase("ScratchSuite." + nameof(TwoClaimsAreTwoDirectories), TwoClaimsAreTwoDirectories),
        new TestCase("ScratchSuite." + nameof(ASubdirectoryLivesUnderTheClaimedDirectory), ASubdirectoryLivesUnderTheClaimedDirectory),
        new TestCase("ScratchSuite." + nameof(RemoveDeletesTheClaimedDirectoryAndNothingElse), RemoveDeletesTheClaimedDirectoryAndNothingElse),
        new TestCase("ScratchSuite." + nameof(RemoveIsSafeWhenTheDirectoryIsAlreadyGone), RemoveIsSafeWhenTheDirectoryIsAlreadyGone),
        new TestCase("ScratchSuite." + nameof(ClaimRefusesAParentOutsideTheRunRoot), ClaimRefusesAParentOutsideTheRunRoot),
        new TestCase("ScratchSuite." + nameof(ClaimRefusesAParentThatOnlyLooksLikeItIsInsideTheRunRoot), ClaimRefusesAParentThatOnlyLooksLikeItIsInsideTheRunRoot),
        new TestCase("ScratchSuite." + nameof(ClaimRefusesAParentThatOnlyLooksLikeItIsInsideUntilItIsResolved), ClaimRefusesAParentThatOnlyLooksLikeItIsInsideUntilItIsResolved),
        new TestCase("ScratchSuite." + nameof(ClaimAcceptsAParentSpelledWithForwardSlashes), ClaimAcceptsAParentSpelledWithForwardSlashes),
        new TestCase("ScratchSuite." + nameof(ClaimRefusesAnEmptyParent), ClaimRefusesAnEmptyParent),
        new TestCase("ScratchSuite." + nameof(ClaimRunRootRefusesASecondRunInOneProcess), ClaimRunRootRefusesASecondRunInOneProcess),
        new TestCase("ScratchSuite." + nameof(AVolumeRootIsItsOwnNormalizedSpelling), AVolumeRootIsItsOwnNormalizedSpelling),
        new TestCase("ScratchSuite." + nameof(ADirectoryUnderAVolumeRootIsNotAVolumeRoot), ADirectoryUnderAVolumeRootIsNotAVolumeRoot),
        new TestCase("ScratchSuite." + nameof(ADriveLetterIsNotAVolumeRoot), ADriveLetterIsNotAVolumeRoot),
        new TestCase("ScratchSuite." + nameof(AShareIsWrittenWithBackslashes), AShareIsWrittenWithBackslashes),
        new TestCase("ScratchSuite." + nameof(RequireRefusesALetterNothingIsMountedOn), RequireRefusesALetterNothingIsMountedOn),
        new TestCase("ScratchSuite." + nameof(AnUnmountedLetterIsFoundRatherThanWrittenDown), AnUnmountedLetterIsFoundRatherThanWrittenDown),
        new TestCase("ScratchSuite." + nameof(AMountedLetterIsSkippedRatherThanActedOn), AMountedLetterIsSkippedRatherThanActedOn),
    ];

    /// <summary>
    /// The name a claim took, so it can be offered to a later claim as one that is already there.
    /// </summary>
    private static string NameOf(ScratchDirectory claim) =>
        Path.GetFileName(Normalize(claim.FullPath));

    /// <summary>
    /// A claimed directory is a direct child of the parent it was handed, which is a directory this run
    /// already owns, so nothing about it depends on where the repository happens to sit.
    /// </summary>
    private static Task ClaimCreatesADirectoryUnderTheParentItWasGiven(TestContext context)
    {
        var parent = ScratchWorkspace.Root;
        var claimed = ScratchDirectory.Claim(parent);

        try
        {
            Check.True(Directory.Exists(claimed.FullPath), $"the claimed directory exists: {claimed.FullPath}");
            Check.Equal(Trimmed(ParentOf(claimed.FullPath)), Trimmed(parent), "and it is a direct child of the parent that was named");
            Check.Equal(parent, claimed.Parent, "which the claim also remembers as the parent it works under");
        }
        finally
        {
            claimed.Remove();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The case the whole mechanism exists for. <c>Directory.CreateDirectory</c> reports success for a
    /// directory that was already there, so a claim that did not check first would hand a case somebody
    /// else's directory — and the claim's own removal would then delete it.
    /// </summary>
    private static Task ClaimNeverReusesADirectoryThatAlreadyExists(TestContext context)
    {
        var parent = ScratchWorkspace.Root;

        // Taken, because this run claimed it first, and holding a file makes it worth taking rather
        // than merely present.
        var bystander = ScratchDirectory.Claim(parent);
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
            claimed = ScratchDirectory.Claim(parent, () => offered.Dequeue());

            Check.Equal(
                0,
                offered.Count,
                "the taken name was offered and the free name was taken in turn, rather than the taken one offered again");
            Check.Equal(
                Normalize(Path.Combine(parent, free)),
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
    }

    /// <summary>
    /// One taken name is a retry; a run of them is a loop, and the claim has to come back with a directory
    /// rather than with the last name it was offered.
    /// </summary>
    private static Task ClaimRegeneratesUntilItFindsAFreeName(TestContext context)
    {
        var parent = ScratchWorkspace.Root;
        var taken = new[] { ScratchDirectory.Claim(parent), ScratchDirectory.Claim(parent) };

        var free = Guid.NewGuid().ToString("N");
        var offered = new Queue<string>([.. taken.Select(NameOf), free]);

        ScratchDirectory? claimed = null;

        try
        {
            claimed = ScratchDirectory.Claim(parent, () => offered.Dequeue());

            Check.Equal(
                Normalize(Path.Combine(parent, free)),
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
    }

    /// <summary>
    /// A machine where every name is taken would otherwise spin here for the rest of the run and report
    /// nothing. Naming the budget is what turns that into a failure that says what happened.
    /// </summary>
    private static Task ClaimGivesUpRatherThanSpin(TestContext context)
    {
        var taken = ScratchDirectory.Claim(ScratchWorkspace.Root);
        var name = NameOf(taken);

        try
        {
            var failure = Check.Throws<InvalidOperationException>(
                () => ScratchDirectory.Claim(taken.Parent, () => name),
                "a name that is never free is a failure, not an endless loop");

            Check.True(failure.Message.Contains(name, StringComparison.Ordinal), $"and it names the name that was taken ({failure.Message})");
        }
        finally
        {
            taken.Remove();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing in the source names the directory, which is the property that makes the claim safe: a name
    /// written into a test is a name the suite could be pointed at twice.
    /// </summary>
    private static Task ClaimNamesADirectoryTheCallerCannotHaveWrittenDown(TestContext context)
    {
        var claimed = ScratchDirectory.Claim(ScratchWorkspace.Root);

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
    }

    /// <summary>Two claims are two directories, so a second case cannot land in the first one's tree.</summary>
    private static Task TwoClaimsAreTwoDirectories(TestContext context)
    {
        var parent = ScratchWorkspace.Root;
        var first = ScratchDirectory.Claim(parent);
        var second = ScratchDirectory.Claim(parent);

        try
        {
            Check.NotEqual(Normalize(first.FullPath), Normalize(second.FullPath), "two claims of the same parent are two directories");
        }
        finally
        {
            first.Remove();
            second.Remove();
        }

        return Task.CompletedTask;
    }

    /// <summary>Consumers share one claim and take a named child of it.</summary>
    private static Task ASubdirectoryLivesUnderTheClaimedDirectory(TestContext context)
    {
        var claimed = ScratchDirectory.Claim(ScratchWorkspace.Root);

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
    }

    /// <summary>
    /// Removing a claim is a recursive delete, so it must delete the path the claim returned and nothing
    /// beside it.
    /// </summary>
    private static Task RemoveDeletesTheClaimedDirectoryAndNothingElse(TestContext context)
    {
        var parent = ScratchWorkspace.Root;

        // A sibling, and a claim like any other: the point is that the path beside the claim survives,
        // and a bystander this run does not own would not be a thing the suite is allowed to make.
        var bystander = ScratchDirectory.Claim(parent);
        var notOurs = Path.Combine(bystander.FullPath, "not-ours.txt");
        File.WriteAllText(notOurs, "written by this test");

        var claimed = ScratchDirectory.Claim(parent);

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
    }

    /// <summary>
    /// Cleanup runs in a <c>finally</c>, sometimes after the case already failed, and twice is what two
    /// cleanup paths mean. Failing on the second would replace the case's own failure with a complaint
    /// about its own cleanup.
    /// </summary>
    private static Task RemoveIsSafeWhenTheDirectoryIsAlreadyGone(TestContext context)
    {
        var claimed = ScratchDirectory.Claim(ScratchWorkspace.Root);

        claimed.Remove();
        claimed.Remove();

        Check.False(Directory.Exists(Normalize(claimed.FullPath)), "removing twice is not a failure");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A claim is taken under a directory this run already owns. Handed the volume root itself, it would
    /// create a directory beside everything the run works in — which is the one thing the working directory
    /// exists to prevent.
    /// </summary>
    private static Task ClaimRefusesAParentOutsideTheRunRoot(TestContext context)
    {
        var volumeRoot = ScratchWorkspace.VolumeRoot;

        var failure = Check.Throws<ArgumentException>(
            () => ScratchDirectory.Claim(volumeRoot),
            "the volume root is outside the run's directory, so nothing is created in it");

        Check.True(
            failure.Message.Contains(volumeRoot, StringComparison.OrdinalIgnoreCase),
            $"and it names the parent it refused ({failure.Message})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A prefix match is not containment. The run root's name is a text prefix of a sibling's name that
    /// begins with it, so a comparison that stopped at the end of the run root would read a directory beside
    /// it as a child of it — and a claim taken there is a claim outside the working directory.
    /// </summary>
    /// <remarks>
    /// The parent is built from a string rather than created, because no run produces it: run root names are
    /// Guids, and a Guid is never a prefix of another. The comparison therefore has to stop at a separator,
    /// which is the one part of containment nothing else in this file can reach.
    /// </remarks>
    private static Task ClaimRefusesAParentThatOnlyLooksLikeItIsInsideTheRunRoot(TestContext context)
    {
        var runRoot = ScratchDirectory.RunRootPath;
        Check.NotNull(runRoot, "the runner has configured the run's directory before a single case runs");

        var withoutSeparator = runRoot!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sibling = withoutSeparator + "-beside-the-run" + Path.DirectorySeparatorChar;

        Check.True(
            sibling.StartsWith(withoutSeparator, StringComparison.OrdinalIgnoreCase),
            $"the parent is a text prefix of the run root's name, which is exactly what makes it a trap ({sibling})");

        var failure = Check.Throws<ArgumentException>(
            () => ScratchDirectory.Claim(sibling),
            "so containment is decided at a separator rather than at the end of the name");

        Check.True(
            failure.Message.Contains(sibling, StringComparison.OrdinalIgnoreCase),
            $"and it names the parent it refused ({failure.Message})");
        Check.False(Directory.Exists(sibling), "and nothing was created beside the run's directory");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A prefix match over text is not containment over paths. <c>..</c> segments resolve, so a parent that
    /// begins with the run root's name can leave it entirely — and a claim taken there is created outside the
    /// working directory, which is the one thing the run's directory exists to prevent.
    /// </summary>
    /// <remarks>
    /// The parent is built from a string, because no caller produces one: every claim this suite makes takes
    /// <see cref="ScratchWorkspace.Root"/> or another claim's parent, and both are already resolved. The
    /// case is here because the case above is a precedent for building a parent by hand, and the next author
    /// to do that is the next person to hand <see cref="ScratchDirectory"/> a traversal.
    /// <para>
    /// The name it climbs to carries this run's own name, so a leftover could not make it fail and only this
    /// run could have created it — the same reasoning <see cref="ScratchDirectory"/> applies to the names it
    /// takes for itself.
    /// </para>
    /// </remarks>
    private static Task ClaimRefusesAParentThatOnlyLooksLikeItIsInsideUntilItIsResolved(TestContext context)
    {
        var runRoot = ScratchDirectory.RunRootPath;
        Check.NotNull(runRoot, "the runner has configured the run's directory before a single case runs");

        var separator = Path.DirectorySeparatorChar;
        var runRootName = Path.GetFileName(runRoot!.TrimEnd(separator, Path.AltDirectorySeparatorChar));
        var besideTheRun = runRootName + "-climbed-out" + separator;
        var climbing = runRoot + ".." + separator + besideTheRun;

        Check.True(
            climbing.StartsWith(runRoot, StringComparison.OrdinalIgnoreCase),
            $"the parent is a text prefix of the run root, which is what a comparison over text would let through ({climbing})");

        var resolved = Path.GetFullPath(climbing);

        Check.False(
            resolved.StartsWith(runRoot, StringComparison.OrdinalIgnoreCase),
            $"and resolving it lands outside the run root anyway, so the prefix is a trap rather than a coincidence ({resolved})");

        var failure = Check.Throws<ArgumentException>(
            () => ScratchDirectory.Claim(climbing),
            "so the parent is resolved before containment is decided, not compared as text");

        Check.True(
            failure.Message.Contains(resolved, StringComparison.OrdinalIgnoreCase),
            $"and it names the directory it would have created under, which is the resolved one ({failure.Message})");
        Check.False(
            Directory.Exists(Path.Combine(ScratchWorkspace.VolumeRoot, besideTheRun)),
            $"and nothing was created in the volume root beside the run's directory ({besideTheRun})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The other half of the same defect. One directory is two spellings, and a claim has to take both: a
    /// parent written with forward slashes resolves to a directory inside the run and nowhere else, so
    /// refusing it turns a correct call into a failure that reads as a broken harness rather than as a
    /// boundary.
    /// </summary>
    /// <remarks>
    /// The fixture is <see cref="ScratchWorkspace.Root"/> with its separators swapped, not a path written
    /// here, so it cannot drift out of the run's directory; and the first assertion proves the two
    /// spellings really differ, so the case cannot quietly stop exercising the spelling it is about.
    /// </remarks>
    private static Task ClaimAcceptsAParentSpelledWithForwardSlashes(TestContext context)
    {
        var spelledWithBackslashes = ScratchWorkspace.Root;
        var spelledWithSlashes = spelledWithBackslashes.Replace(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        Check.NotEqual(
            spelledWithBackslashes,
            spelledWithSlashes,
            "the two spellings differ, so this case is about the spelling rather than about a path that reads the same either way");

        var before = Directory.GetDirectories(spelledWithBackslashes).Length;
        var claimed = ScratchDirectory.Claim(spelledWithSlashes);

        try
        {
            Check.Equal(
                spelledWithBackslashes,
                claimed.Parent,
                "the claim keeps the parent in the one spelling the filesystem uses, rather than the one it was handed");
            Check.True(Directory.Exists(claimed.FullPath), $"the claimed directory exists: {claimed.FullPath}");
            Check.Equal(
                Trimmed(ParentOf(claimed.FullPath)),
                Trimmed(spelledWithBackslashes),
                "and it is a direct child of the same directory the backslash spelling names");

            claimed.Remove();

            Check.False(Directory.Exists(Normalize(claimed.FullPath)), "removing it takes the directory away again");
            Check.Equal(
                before,
                Directory.GetDirectories(spelledWithBackslashes).Length,
                "and leaves nothing of its own behind in the parent");
        }
        finally
        {
            claimed.Remove();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// An empty parent is the shape of the mistake this check exists to catch, reduced to its shortest form:
    /// nothing was named, so nothing can be inside the run's directory.
    /// </summary>
    private static Task ClaimRefusesAnEmptyParent(TestContext context)
    {
        var failure = Check.Throws<ArgumentException>(
            () => ScratchDirectory.Claim(string.Empty),
            "a parent that names no directory is refused rather than resolved against the current one");

        Check.True(
            failure.Message.Contains("parent", StringComparison.OrdinalIgnoreCase),
            $"and it says which argument was missing ({failure.Message})");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The run's own directory is claimed once. A second claim would put a directory beside the one every
    /// case writes under, so the route out of the working directory is closed from both ends.
    /// </summary>
    private static Task ClaimRunRootRefusesASecondRunInOneProcess(TestContext context)
    {
        var failure = Check.Throws<InvalidOperationException>(
            () => ScratchDirectory.ClaimRunRoot(ScratchWorkspace.VolumeRoot),
            "the run's directory is claimed once per process");

        Check.True(
            failure.Message.Contains(ScratchWorkspace.VolumeRoot, StringComparison.OrdinalIgnoreCase),
            $"and it names the root it refused ({failure.Message})");

        return Task.CompletedTask;
    }

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
    /// <c>Path.GetPathRoot</c> answers <c>W:</c> for a bare letter, and that is drive-relative: what it
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
    /// Neither assertion can tell a looked-up letter from a written-down one — <c>'W'</c> comes out of
    /// the lookup whenever <c>W:</c> is genuinely free, and that is the lookup working rather than the
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
    /// parent of <c>W:\</c> it answers <c>W:</c>, and asked for the parent of a directory on a share it
    /// answers without one.
    /// </summary>
    private static string Trimmed(string path) => Normalize(path);
}
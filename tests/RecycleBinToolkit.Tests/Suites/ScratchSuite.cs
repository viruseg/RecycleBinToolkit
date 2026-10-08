using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// What the suite writes to, and how it comes to own it.
/// </summary>
/// <remarks>
/// Every case in this repository writes files and deletes directories. The only thing standing between
/// those two facts and somebody's data is the rule these cases pin: a directory is this suite's because
/// this run created it under a name that did not exist, and it is removed because the suite remembers the
/// exact path it created. A name written into the source is not that — it is a name somebody else may
/// already be using, and <c>Directory.CreateDirectory</c> answers an existing directory with success.
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
    ];

    /// <summary>
    /// A claimed directory is a direct child of the volume root the caller named, so nothing about it
    /// depends on where the repository happens to sit.
    /// </summary>
    private static Task ClaimCreatesADirectoryInsideTheVolumeRoot(TestContext context)
    {
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var claimed = ScratchDirectory.Claim(volumeRoot);

        try
        {
            Check.True(Directory.Exists(claimed.FullPath), $"the claimed directory exists: {claimed.FullPath}");
            Check.Equal(ParentOf(volumeRoot), ParentOf(claimed.FullPath), "and it is a direct child of the root that was named");
            Check.Equal(volumeRoot, claimed.Volume, "which the claim also remembers as the volume it works on");
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
        const string TakenName = "ScratchSuite-bystander";

        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var bystander = Path.Combine(volumeRoot, TakenName);

        Directory.CreateDirectory(bystander);
        File.WriteAllText(Path.Combine(bystander, "theirs.txt"), "somebody else's data");

        ScratchDirectory? claimed = null;

        try
        {
            // Offered the taken name first and a free one second, so the claim has to move past the one it
            // was handed rather than retry it. A seam that only ever offers the taken name would instead be
            // the give-up case, which is what ClaimGivesUpRatherThanSpin asserts.
            var offered = new Queue<string>([TakenName, "ScratchSuite-bystander-2"]);

            claimed = ScratchDirectory.Claim(volumeRoot, () => offered.Dequeue());

            Check.Equal(
                0,
                offered.Count,
                "the taken name was offered and the second name was taken in turn, rather than the taken one offered again");
            Check.NotEqual(
                Normalize(claimed.FullPath),
                Normalize(bystander),
                "a name that was already taken is not adopted, however it came to be proposed");
            Check.True(File.Exists(Path.Combine(bystander, "theirs.txt")), "and the directory that was there keeps its file");
        }
        finally
        {
            if (claimed is not null)
            {
                claimed.Remove();
            }

            Directory.Delete(bystander, recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One taken name is a retry; a run of them is a loop, and the claim has to come back with a directory
    /// rather than with the last name it was offered.
    /// </summary>
    private static Task ClaimRegeneratesUntilItFindsAFreeName(TestContext context)
    {
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var taken = new[] { "ScratchSuite-taken-a", "ScratchSuite-taken-b" };
        var offered = new Queue<string>([.. taken, "ScratchSuite-free"]);

        foreach (var name in taken)
        {
            Directory.CreateDirectory(Path.Combine(volumeRoot, name));
        }

        ScratchDirectory? claimed = null;

        try
        {
            claimed = ScratchDirectory.Claim(volumeRoot, () => offered.Dequeue());

            Check.Equal(
                Normalize(Path.Combine(volumeRoot, "ScratchSuite-free")),
                Normalize(claimed.FullPath),
                "the claim settles for the first name that was free, not the last one offered");
            Check.Equal(0, offered.Count, "and it stops asking once it has one");
        }
        finally
        {
            if (claimed is not null)
            {
                claimed.Remove();
            }

            foreach (var name in taken)
            {
                Directory.Delete(Path.Combine(volumeRoot, name), recursive: true);
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
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var taken = "ScratchSuite-always-taken";

        Directory.CreateDirectory(Path.Combine(volumeRoot, taken));

        try
        {
            var failure = Check.Throws<InvalidOperationException>(
                () => ScratchDirectory.Claim(volumeRoot, () => taken),
                "a name that is never free is a failure, not an endless loop");

            Check.True(failure.Message.Contains(taken, StringComparison.Ordinal), $"and it names the name that was taken ({failure.Message})");
        }
        finally
        {
            Directory.Delete(Path.Combine(volumeRoot, taken), recursive: true);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Nothing in the source names the directory, which is the property that makes the claim safe: a name
    /// written into a test is a name the suite could be pointed at twice.
    /// </summary>
    private static Task ClaimNamesADirectoryTheCallerCannotHaveWrittenDown(TestContext context)
    {
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var claimed = ScratchDirectory.Claim(volumeRoot);

        try
        {
            var name = Path.GetFileName(Normalize(claimed.FullPath));

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
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var first = ScratchDirectory.Claim(volumeRoot);
        var second = ScratchDirectory.Claim(volumeRoot);

        try
        {
            Check.NotEqual(Normalize(first.FullPath), Normalize(second.FullPath), "two claims of the same root are two directories");

            first.Remove();
            second.Remove();
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
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var claimed = ScratchDirectory.Claim(volumeRoot);

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
    /// on the volume beside it.
    /// </summary>
    private static Task RemoveDeletesTheClaimedDirectoryAndNothingElse(TestContext context)
    {
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var bystander = Path.Combine(volumeRoot, "ScratchSuite-bystander");
        var claimed = ScratchDirectory.Claim(volumeRoot);

        Directory.CreateDirectory(bystander);
        File.WriteAllText(Path.Combine(bystander, "theirs.txt"), "not ours");

        try
        {
            File.WriteAllText(Path.Combine(claimed.FullPath, "ours.txt"), "scratch");

            claimed.Remove();

            Check.False(Directory.Exists(Normalize(claimed.FullPath)), "the claimed directory is gone");
            Check.True(File.Exists(Path.Combine(bystander, "theirs.txt")), "and the directory beside it is untouched");
        }
        finally
        {
            claimed.Remove();
            Directory.Delete(bystander, recursive: true);
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
        var volumeRoot = ScratchWorkspace.VolumeRoot;
        var claimed = ScratchDirectory.Claim(volumeRoot);

        claimed.Remove();
        claimed.Remove();

        Check.False(Directory.Exists(Normalize(claimed.FullPath)), "removing twice is not a failure");

        return Task.CompletedTask;
    }

    /// <summary>
    /// A claim is a direct child of a volume root. Handed a directory instead, it would create the claim
    /// inside the caller's data — and its removal would then take that data's parent with it.
    /// </summary>
    private static Task ClaimRefusesAParentThatIsNotAVolumeRoot(TestContext context)
    {
        var directory = Path.Combine(ScratchWorkspace.VolumeRoot, "ScratchSuite-not-a-root");

        Directory.CreateDirectory(directory);

        try
        {
            var failure = Check.Throws<ArgumentException>(
                () => ScratchDirectory.Claim(directory),
                "a directory is not a volume root, so nothing is created inside it");

            Check.True(failure.Message.Contains("volume root", StringComparison.OrdinalIgnoreCase), $"and says why ({failure.Message})");
            Check.Equal(
                0,
                Directory.GetDirectories(directory).Length,
                "and the caller's directory still holds nothing this suite put there");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

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
    /// <c>Path.GetPathRoot</c> answers <c>X:</c> for a bare letter, and that is drive-relative: what it
    /// names is whichever directory the process happens to have current for that drive.
    /// </summary>
    private static Task ADriveLetterIsNotAVolumeRoot(TestContext context)
    {
        var letter = SubstitutedVolume.AbsentVolumeLetter;

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
        var letter = SubstitutedVolume.AbsentVolumeLetter;
        var root = $"{letter}:{Path.DirectorySeparatorChar}";

        var failure = Check.Throws<InvalidOperationException>(
            () => VolumeRoot.Require(root, "--scratch"),
            "a root the machine does not have is not somewhere to write");

        Check.True(failure.Message.Contains(root, StringComparison.Ordinal), $"and it names the root that was refused ({failure.Message})");

        return Task.CompletedTask;
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The directory a path sits in, as the filesystem spells it: a drive root keeps its separator, a
    /// directory on a share does not.
    /// </summary>
    private static string ParentOf(string path) => Directory.GetParent(Normalize(path))?.FullName ?? Normalize(path);
}
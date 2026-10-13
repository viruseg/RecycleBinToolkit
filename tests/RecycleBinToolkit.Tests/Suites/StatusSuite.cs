using System.Runtime.Versioning;
using Microsoft.Win32;
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

/// <summary>
/// Covers <see cref="RecycleBin.GetStatusAsync"/>: whether the Recycle Bin is in use for a volume,
/// and whether the answer matches what that volume actually does.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
internal static class StatusSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("StatusSuite." + nameof(StatusAgreesWithAVolumeThatJustAcceptedADelete), StatusAgreesWithAVolumeThatJustAcceptedADelete),
        new TestCase("StatusSuite." + nameof(StatusReportsAnAbsentVolumeAsNotFound), StatusReportsAnAbsentVolumeAsNotFound),
        new TestCase("StatusSuite." + nameof(StatusReportsAPathThatIsNotAVolumeAsNotFound), StatusReportsAPathThatIsNotAVolumeAsNotFound),
        new TestCase("StatusSuite." + nameof(StatusSeparatesTheReasonsItCanReport), StatusSeparatesTheReasonsItCanReport),
        new TestCase("StatusSuite." + nameof(StatusNeverReportsEnabledWithoutAFact), StatusNeverReportsEnabledWithoutAFact),
        new TestCase("StatusSuite." + nameof(StatusAgreesWhateverSpellingOfTheRootItIsGiven), StatusAgreesWhateverSpellingOfTheRootItIsGiven),
        new TestCase("StatusSuite." + nameof(ExtendedLengthSpellingOfTheRootIsTheSameVolume), ExtendedLengthSpellingOfTheRootIsTheSameVolume),
        new TestCase("StatusSuite." + nameof(TheVolumeKeyIsNamedTheWayWindowsNamesIt), TheVolumeKeyIsNamedTheWayWindowsNamesIt),
        new TestCase("StatusSuite." + nameof(AnUnconfiguredVolumeIsIndeterminateNotNamed), AnUnconfiguredVolumeIsIndeterminateNotNamed),
        new TestCase("StatusSuite." + nameof(AMissingPerVolumeKeyIsNotAnEnabledVolume), AMissingPerVolumeKeyIsNotAnEnabledVolume),
        new TestCase("StatusSuite." + nameof(OneShareHasOneSpelling), OneShareHasOneSpelling),
        new TestCase("StatusSuite." + nameof(AnUncRootIsRecognised), AnUncRootIsRecognised),
    ];

    /// <summary>
    /// Whether a path names a network share, decided from the path itself and not from the machine.
    /// </summary>
    /// <remarks>
    /// <see cref="DriveInfo"/> throws <see cref="ArgumentException"/> on a UNC path instead of reporting
    /// <see cref="DriveType.Network"/>, so a caller that asks it leaves the type
    /// <see cref="DriveType.Unknown"/> and the volume
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/> — a guess, when the path already names
    /// a remote share. The prefix has to come off before the test: <c>\\?\UNC\server\share</c> names a
    /// share as plainly as <c>\\server\share</c> does.
    /// </remarks>
    private static Task AnUncRootIsRecognised(TestContext context)
    {
        Check.True(RecycleBinVolumeState.IsUncRoot(@"\\server\share\"), @"a share with its trailing separator is a UNC root");
        Check.True(RecycleBinVolumeState.IsUncRoot(@"\\server\share"), "the same share without the separator is still a UNC root");
        Check.True(RecycleBinVolumeState.IsUncRoot(@"\\?\UNC\server\share\"), "the extended-length spelling of a share is still a UNC root, prefix and all");

        Check.False(RecycleBinVolumeState.IsUncRoot(@"W:\"), @"a drive root is not a UNC root");
        Check.False(RecycleBinVolumeState.IsUncRoot("W:"), "and neither is a bare drive letter");
        Check.False(RecycleBinVolumeState.IsUncRoot(Path.Combine("relative", "path")), "and neither is a relative path");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every spelling of one share names one volume, so every spelling has to normalise to the same
    /// root and pass the same root test. They did not: <see cref="Path.GetPathRoot(string)"/> answers a
    /// UNC path without its trailing separator whether or not the caller typed one, and hands back an
    /// extended-length root with the <c>\\?\UNC\</c> prefix still attached — so the same share came back
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/> with the separator and
    /// <see cref="RecycleBinAvailabilityReason.VolumeNotFound"/> without it.
    /// </summary>
    /// <remarks>
    /// Pure, and reachable without a share: every string here is a name, so nothing is read and no
    /// network is touched. That is also what lets the answer be about spelling alone.
    /// </remarks>
    private static Task OneShareHasOneSpelling(TestContext context)
    {
        var share = @"\\server\share";
        var expected = share + Path.DirectorySeparatorChar;

        foreach (var spelling in new[] { share, share + Path.DirectorySeparatorChar, @"\\?\UNC\server\share\" })
        {
            Check.Equal(
                expected,
                RecycleBinVolumeState.NormalizeVolumeRoot(spelling),
                $"'{spelling}' normalises to the share root, with its trailing separator and no prefix");

            Check.True(
                RecycleBinVolumeState.IsVolumeRoot(spelling),
                $"'{spelling}' is recognised as the root of a volume rather than as a path inside one");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A volume nothing was recorded for answers <see cref="RecycleBinAvailabilityReason.Indeterminate"/>,
    /// not <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/>. Unproven is not enabled, and
    /// it is not a statement about a missing bin either: nothing walks the bin to check.
    /// </summary>
    /// <remarks>
    /// The volume is the one <c>--scratch</c> named, asked about rather than built. What is given up is
    /// an assertion about a DOS device of this library's own making; what is gained is that the reason
    /// asserted is one a caller is actually handed — a flash drive, or a share, rather than a stand-in
    /// for one.
    /// <para>
    /// The gate is coarser than the claim, and the third assertion is what makes up the difference.
    /// <see cref="ScratchWorkspace.RequireNonRecycling"/> skips on every reason but
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/>, so it lets this case run on a share
    /// (<see cref="RecycleBinAvailabilityReason.UnsupportedVolumeType"/>) and on a volume whose own
    /// setting turns recycling off (<see cref="RecycleBinAvailabilityReason.DisabledByUserPreference"/>)
    /// — both of which are decided, and both of which the case is right to pass on. It also lets it run on
    /// a volume whose type could not be read at all
    /// (<see cref="RecycleBinAvailabilityReason.Indeterminate"/>), and there the assertions hold without
    /// witnessing anything beyond the undecided reason itself.
    /// </para>
    /// <para>
    /// The last assertion pins the two readings of the volume together — the run's cached
    /// <see cref="ScratchWorkspace.Reason"/> and a fresh one here — because a disagreement would mean the
    /// answer every other case in this run gates on no longer describes its own volume.
    /// </para>
    /// </remarks>
    private static async Task AnUnconfiguredVolumeIsIndeterminateNotNamed(TestContext context)
    {
        ScratchWorkspace.RequireNonRecycling("a volume with no Recycle Bin");

        var status = await RecycleBin.GetStatusAsync(ScratchWorkspace.VolumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(ScratchWorkspace.VolumeRoot, status.VolumeRoot, "the answer names the volume it was asked about");
        Check.False(status.IsEnabled, $"a volume with no Recycle Bin is not an enabled Recycle Bin (got {status.Reason})");

        if (status.Reason is not RecycleBinAvailabilityReason.Indeterminate)
        {
            Check.NotEqual(
                RecycleBinAvailabilityReason.NoRecycleBinOnVolume,
                status.Reason,
                "a decided reason is one the library committed to, not a guess about a missing bin");
        }

        Check.Equal(
            status.Reason,
            ScratchWorkspace.Reason,
            "and the run's cached reason agrees with a fresh reading of the same volume");
    }

    /// <summary>
    /// A volume Windows never configured for recycling is not a volume whose recycling is switched on.
    /// The per-volume setting has to answer three ways, because a value that is absent is a different
    /// fact from a value that reads zero, and reading both as "does not delete outright" reports a
    /// volume that has no bin as one that does.
    /// </summary>
    /// <remarks>
    /// Two shapes leave the setting unknown, and they are not the same fact. A volume with no GUID at all
    /// has no subkey name to read; a real volume whose GUID resolves can still have no key under the
    /// volume list, because Windows writes that key only for volumes it has configured a bin for. The
    /// second is the one that loses files, and it is the shape this case reaches with a freshly minted
    /// GUID — a name the volume list cannot contain, so the check reads and writes nothing and leaves
    /// nothing to clean up.
    /// <para>
    /// The first shape was reached through a <c>subst</c> mapping, which reported a drive type that
    /// supports a bin while having no GUID to key the setting on. Nothing builds such a volume any more,
    /// and nothing asserts that shape either — this is not a claim that is covered elsewhere.
    /// <see cref="RecycleBinRegistry.ReadNukeOnDelete"/>'s null-key-name branch is now reached by no case
    /// in this repository, so a regression that gave a GUID-less volume any answer other than Unknown would
    /// be caught by neither this case nor any other. The gap is recorded here rather than closed, because
    /// closing it needs a volume with no GUID and nothing in this repository makes one.
    /// </para>
    /// <para>
    /// The last assertion pins that the unknown cannot be honoured anywhere downstream of the reader.
    /// <see cref="RecycleBinVolumePolicy.Classify"/> still answers Enabled for a fixed volume with no
    /// overrides, so Unknown has to survive as a three-state value reaching the caller rather than being
    /// flattened to a bool on the way.
    /// </para>
    /// </remarks>
    private static Task AMissingPerVolumeKeyIsNotAnEnabledVolume(TestContext context)
    {
        Check.Equal(
            RecycleBinVolumePolicy.VolumeRecycleSetting.Unknown,
            RecycleBinRegistry.ReadNukeOnDelete(Guid.NewGuid().ToString("B")),
            "a volume GUID with no key under the volume list is unknown, not a volume that recycles");

        Check.Equal(
            RecycleBinAvailabilityReason.Enabled,
            RecycleBinVolumePolicy.Classify(
                volumeExists: true,
                DriveType.Fixed,
                disabledByPolicy: false,
                nukeOnDelete: false),
            "the decision still cannot see an unknown setting, so a three-state answer has to reach the caller for a volume with no key to stop reading as enabled");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The per-volume setting is read from a subkey under the Recycle Bin's own volume list, so the
    /// name the library derives has to be one Windows actually wrote. Deriving it as the
    /// <c>\\?\Volume{…}\</c> device path instead of the bare GUID in braces makes every read miss, and
    /// a volume whose recycling is switched off is then reported as enabled.
    /// </summary>
    /// <remarks>
    /// The comparison is against the machine's own key list rather than against the library's
    /// composition of it, so it pins the name Windows uses instead of restating what the code builds.
    /// </remarks>
    private static async Task TheVolumeKeyIsNamedTheWayWindowsNamesIt(TestContext context)
    {
        ScratchWorkspace.RequireRecycling("a volume Windows has configured a bin for, so it has written the key this case reads");

        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var keyName = RecycleBinVolumeState.TryGetVolumeGuidKeyName(volumeRoot);
        Check.NotNull(keyName, $"the volume GUID resolves for '{volumeRoot}'");
        Check.False(keyName!.Contains('\0'), $"the name carries no embedded terminator (got '{Escape(keyName)}')");
        Check.Equal('{', keyName[0], "the name is the GUID in braces, which is what the subkey is called");
        Check.True(keyName.EndsWith('}'), "and it ends with the closing brace");

        using var volumes = Registry.CurrentUser.OpenSubKey(BitBucketVolumeList);
        Check.NotNull(volumes, "Windows keeps its per-volume Recycle Bin settings under the current user");

        var written = volumes!.GetSubKeyNames();
        Check.NotEqual(0, written.Length, $"there is at least one volume recorded under {BitBucketVolumeList}");

        Check.True(
            Array.Exists(written, name => string.Equals(name, keyName, StringComparison.OrdinalIgnoreCase)),
            $"the key Windows wrote is the one the library reads (derived '{Escape(keyName)}')");
    }

    /// <summary>
    /// An extended-length root names the same volume as its plain spelling, so it has to produce the
    /// same answer. <see cref="DriveInfo"/> refuses the prefixed form outright, so without stripping the
    /// answer collapses to Indeterminate for a volume that is plainly there.
    /// </summary>
    private static async Task ExtendedLengthSpellingOfTheRootIsTheSameVolume(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;
        var plain = await RecycleBin.GetStatusAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);
        var extended = await RecycleBin.GetStatusAsync(ExtendedPath.For(volumeRoot), context.CancellationToken).ConfigureAwait(false);

        Check.Equal(plain.VolumeRoot, extended.VolumeRoot, "both spellings name the same volume root");
        Check.Equal(plain.DriveType, extended.DriveType, "and the same drive type, so the volume is not unreadable");
        Check.Equal(plain.Reason, extended.Reason, "and the same reason");

        workspace.Dispose();
    }

    private const string BitBucketVolumeList = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    private static string Escape(string value) => value.Replace("\0", "<NUL>");

    /// <summary>
    /// A caller holding a volume as <c>W:</c> and a caller holding the same volume as <c>W:\</c> are
    /// asking the same question, and have to get the same answer.
    /// </summary>
    /// <remarks>
    /// <c>Path.GetPathRoot("W:")</c> is <c>W:</c>, which is a drive-relative path rather than a root. It
    /// satisfies an equality check against itself while being useless to
    /// <c>GetVolumeNameForVolumeMountPoint</c>, which needs the trailing separator — so the volume GUID
    /// came back unreadable and the answer degraded to Indeterminate for a volume that is plainly there.
    /// <para>
    /// What the two spellings agree on is the whole claim: the bare letter and the root with its separator
    /// are the same volume and are answered the same way. That both of them answer <c>Enabled</c> is a
    /// different claim about a different thing — that a volume recycles — and it is made by
    /// <see cref="StatusAgreesWithAVolumeThatJustAcceptedADelete"/>, which establishes a delete before
    /// asserting it. Asserting it here as well would have claimed a recycle this case never made.
    /// </para>
    /// </remarks>
    private static async Task StatusAgreesWhateverSpellingOfTheRootItIsGiven(TestContext context)
    {
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var withSeparator = await RecycleBin.GetStatusAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);
        var bare = await RecycleBin.GetStatusAsync(volumeRoot.TrimEnd(Path.DirectorySeparatorChar), context.CancellationToken)
            .ConfigureAwait(false);

        Check.Equal(withSeparator.VolumeRoot, bare.VolumeRoot, "both spellings report the same volume root");
        Check.Equal(withSeparator.DriveType, bare.DriveType, "and the same drive type");
        Check.Equal(withSeparator.Reason, bare.Reason, "and the same reason, so the answer does not depend on the spelling");
    }

    /// <summary>
    /// The registry can say one thing while the volume does another, so the answer is checked against
    /// observed behaviour: a volume that accepted a delete into the Recycle Bin cannot be reported as
    /// disabled. This is the test that would catch the policy paths disagreeing with the volume.
    /// </summary>
    private static async Task StatusAgreesWithAVolumeThatJustAcceptedADelete(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        ScratchWorkspace.RequireRecycling("a volume that accepts the delete into a Recycle Bin before being asked about it");
        var files = workspace.CreateRandomFiles(1, 4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await RecycleBin
            .RecycleAsync(files, RecycleOptions.Default, cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        Check.True(recycled.Succeeded, "the file was recycled, so the volume demonstrably uses the Recycle Bin");

        var status = await RecycleBin.GetStatusAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.True(status.IsEnabled, $"a volume that just accepted a recycle is enabled (got {status.Reason})");
        Check.Equal(RecycleBinAvailabilityReason.Enabled, status.Reason, "the reason is the plain enabled one");
        Check.Equal(volumeRoot, status.VolumeRoot, "the answer names the volume that was asked about");

        workspace.Dispose();
    }

    /// <summary>
    /// An unmounted drive letter is not a disabled Recycle Bin, and telling a caller otherwise would
    /// send it looking for a policy that is not there.
    /// </summary>
    private static async Task StatusReportsAnAbsentVolumeAsNotFound(TestContext context)
    {
        var letter = AbsentVolume.Letter();
        AbsentVolume.RequireAbsent(letter, "asking about a volume that is not there");

        var status = await RecycleBin.GetStatusAsync(AbsentVolume.RootOf(letter), context.CancellationToken).ConfigureAwait(false);

        Check.False(status.IsEnabled, "a volume that is not mounted is not an enabled Recycle Bin");
        Check.Equal(RecycleBinAvailabilityReason.VolumeNotFound, status.Reason, "the reason is that the volume does not exist");
    }

    /// <summary>
    /// A directory inside a volume is not a volume. Accepting it would answer a question the caller
    /// did not ask.
    /// </summary>
    private static async Task StatusReportsAPathThatIsNotAVolumeAsNotFound(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var status = await RecycleBin.GetStatusAsync(ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

        Check.False(status.IsEnabled, "a path that is not a volume root has no Recycle Bin of its own");
        Check.Equal(RecycleBinAvailabilityReason.VolumeNotFound, status.Reason, "the reason is that it is not a volume root");
    }

    /// <summary>
    /// Each outcome the answer can carry is pinned, because "disabled" is not one piece of information.
    /// A caller that is told the bin is off must be able to tell an administrator's policy from the
    /// user's own setting from a volume type that never had one.
    /// </summary>
    private static Task StatusSeparatesTheReasonsItCanReport(TestContext context)
    {
        Check.Equal(
            RecycleBinAvailabilityReason.Enabled,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Fixed, disabledByPolicy: false, nukeOnDelete: false),
            "a fixed volume with no overrides is enabled");

        Check.Equal(
            RecycleBinAvailabilityReason.DisabledByPolicy,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Fixed, disabledByPolicy: true, nukeOnDelete: false),
            "a policy wins over everything else, because the user cannot override it");

        Check.Equal(
            RecycleBinAvailabilityReason.DisabledByPolicy,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Network, disabledByPolicy: true, nukeOnDelete: false),
            "a policy is still named first when the volume type also rules the bin out");

        Check.Equal(
            RecycleBinAvailabilityReason.DisabledByUserPreference,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Fixed, disabledByPolicy: false, nukeOnDelete: true),
            "a per-volume delete setting is the user's own, not the administrator's");

        Check.Equal(
            RecycleBinAvailabilityReason.UnsupportedVolumeType,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Network, disabledByPolicy: false, nukeOnDelete: false),
            "a network drive has no Recycle Bin at all");

        Check.Equal(
            RecycleBinAvailabilityReason.Enabled,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Removable, disabledByPolicy: false, nukeOnDelete: false),
            "a removable drive is judged on its own settings, not excluded by type");

        Check.Equal(
            RecycleBinAvailabilityReason.VolumeNotFound,
            RecycleBinVolumePolicy.Classify(volumeExists: false, DriveType.Unknown, disabledByPolicy: false, nukeOnDelete: false),
            "a volume that does not exist is not classified at all");

        Check.Equal(
            RecycleBinAvailabilityReason.Indeterminate,
            RecycleBinVolumePolicy.Classify(volumeExists: true, DriveType.Unknown, disabledByPolicy: false, nukeOnDelete: false),
            "a volume whose type could not be read is not guessed to be enabled");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The failure modes this guards are the expensive ones. A volume GUID that cannot be read leaves
    /// the per-volume setting unknown, and answering "enabled" there would be a guess presented as a
    /// fact — which is the same mistake this library exists to stop elsewhere.
    /// </summary>
    private static Task StatusNeverReportsEnabledWithoutAFact(TestContext context)
    {
        foreach (var (volumeExists, driveType, policy, nuke) in Reasons())
        {
            var reason = RecycleBinVolumePolicy.Classify(volumeExists, driveType, policy, nuke);

            if (reason != RecycleBinAvailabilityReason.Enabled)
            {
                continue;
            }

            var justified =
                volumeExists &&
                driveType is DriveType.Fixed or DriveType.Removable &&
                !policy &&
                !nuke;

            if (justified)
            {
                continue;
            }

            throw new CheckFailedException(
                $"expected a reason other than Enabled for volumeExists={volumeExists}, driveType={driveType}, " +
                $"policy={policy}, nukeOnDelete={nuke}, but got {reason}");
        }

        return Task.CompletedTask;
    }

    private static IEnumerable<(bool VolumeExists, DriveType DriveType, bool Policy, bool Nuke)> Reasons()
    {
        bool[] flags = [false, true];

        foreach (var volumeExists in flags)
        {
            foreach (var driveType in new[] { DriveType.Fixed, DriveType.Removable, DriveType.Network, DriveType.Unknown })
            {
                foreach (var policy in flags)
                {
                    foreach (var nuke in flags)
                    {
                        yield return (volumeExists, driveType, policy, nuke);
                    }
                }
            }
        }
    }
}

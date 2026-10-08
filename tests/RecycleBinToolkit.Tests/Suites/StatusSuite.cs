using System.ComponentModel;
using System.Diagnostics;
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
[SupportedOSPlatform("windows6.0.6000")]
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
        new TestCase("StatusSuite." + nameof(NoRecycleBinVolumeIsNamedNotIndeterminate), NoRecycleBinVolumeIsNamedNotIndeterminate),
        new TestCase("StatusSuite." + nameof(AMissingPerVolumeKeyIsNotAnEnabledVolume), AMissingPerVolumeKeyIsNotAnEnabledVolume),
        ];

    /// <summary>
    /// A volume the Shell reports no Recycle Bin for is named as such, not left as Indeterminate. The
    /// two are different problems for the caller, because only one of them says the next delete on that
    /// volume is a permanent deletion.
    /// </summary>
    /// <remarks>
    /// The volume is a <c>subst</c> mapping of the scratch root, which is the one shape of a bin-less
    /// volume that can be produced on any machine. A substituted drive is a DOS device over a real
    /// volume, so it reports a drive type that supports a Recycle Bin, yet it has no GUID for the
    /// per-volume setting to be keyed on and no bin for the Shell to query — <c>SHQueryRecycleBin</c>
    /// answers <c>0x80070003</c> for it, as it does for a network share.
    /// </remarks>
    private static async Task NoRecycleBinVolumeIsNamedNotIndeterminate(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = FirstUnusedDriveLetter();

        try
        {
            await Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var volumeRoot = letter + ":" + Path.DirectorySeparatorChar;

            Check.True(
                RecycleBinVolumeState.TryGetVolumeGuidKeyName(volumeRoot) is null,
                "a substituted drive is the shape under test: there is no volume GUID for the per-volume setting to be read from");

            var status = await RecycleBin.GetStatusAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);

            Check.Equal(volumeRoot, status.VolumeRoot, "the answer names the substituted volume");
            Check.False(status.IsEnabled, $"a volume with no Recycle Bin is not an enabled Recycle Bin (got {status.Reason})");
            Check.Equal(
                RecycleBinAvailabilityReason.NoRecycleBinOnVolume,
                status.Reason,
                "the Shell that reported no bin is named as the reason, rather than the volume being left undecided");
        }
        finally
        {
            await RemoveSubstitute(letter).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A volume Windows never configured for recycling is not a volume whose recycling is switched on.
    /// The per-volume setting has to answer three ways, because a value that is absent is a different
    /// fact from a value that reads zero, and reading both as "does not delete outright" reports a
    /// volume that has no bin as one that does.
    /// </summary>
    /// <remarks>
    /// Two shapes produce an unknown setting, and only the second is the one that loses files. A
    /// <c>subst</c> mapping has no GUID at all, so there is no subkey name to read; a real volume whose
    /// GUID resolves can still have no key under the volume list, because Windows writes that key only
    /// for volumes it has configured a bin for. The second shape is checked with a freshly minted GUID,
    /// which the volume list cannot contain — so the check is read-only, writes nothing, and leaves
    /// nothing to clean up.
    /// <para>
    /// The last assertion pins that this cannot be honoured anywhere downstream of the reader.
    /// <see cref="RecycleBinVolumePolicy.Classify"/> still answers Enabled for a fixed volume with no
    /// overrides, so Unknown has to survive as a three-state value reaching the caller rather than
    /// being flattened to a bool on the way.
    /// </para>
    /// </remarks>
    private static async Task AMissingPerVolumeKeyIsNotAnEnabledVolume(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var letter = FirstUnusedDriveLetter();

        try
        {
            await Substitute(letter, ScratchWorkspace.Root, context.CancellationToken).ConfigureAwait(false);

            var volumeRoot = letter + ":" + Path.DirectorySeparatorChar;
            var keyName = RecycleBinVolumeState.TryGetVolumeGuidKeyName(volumeRoot);

            Check.Equal(
                RecycleBinVolumePolicy.VolumeRecycleSetting.Unknown,
                RecycleBinRegistry.ReadNukeOnDelete(keyName),
                "a volume whose per-volume setting cannot be looked up is unknown, not a volume that recycles");

            // The volume API resolving a GUID says nothing about whether the volume recycles, and this
            // is the shape the subst case above cannot reach: a name the volume list is free to not
            // contain. A GUID minted here is such a name by construction, and no key is written or
            // deleted, so the check is read-only and leaves nothing behind.
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
        }
        finally
        {
            await RemoveSubstitute(letter).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The letter four cases across three suites use to mean "a volume that is not mounted".
    /// </summary>
    /// <remarks>
    /// It is the one letter the substitute case must leave alone. A mapping left behind on it would turn
    /// every one of those cases into a question about a real volume, and the failures would name the
    /// Recycle Bin rather than the leak that caused them.
    /// </remarks>
    private const char AbsentVolumeLetter = 'Z';

    /// <summary>
    /// The highest drive letter nothing is mounted on and no other case has claimed, so a mapping cannot
    /// collide with a real volume, a network drive, or a mapping an earlier case leaked.
    /// </summary>
    private static char FirstUnusedDriveLetter()
    {
        var unavailable = new HashSet<char>(
            DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])))
        {
            AbsentVolumeLetter,
        };

        for (var letter = 'Z'; letter >= 'A'; letter--)
        {
            if (!unavailable.Contains(letter))
            {
                return letter;
            }
        }

        throw new CheckFailedException("every drive letter from Z down to A is taken, so there is nowhere left to substitute");
    }

    /// <summary>
    /// Maps <paramref name="target"/> onto the bare drive letter <paramref name="letter"/> through
    /// <c>subst</c>.
    /// </summary>
    /// <remarks>
    /// The exit code is asserted rather than the mapping's presence, because a refused <c>subst</c>
    /// leaves the letter unmounted, and this case would then report
    /// <see cref="RecycleBinAvailabilityReason.VolumeNotFound"/> for a volume it meant to find bin-less.
    /// It cannot pass that way — the required reason is not that one — but it can be confusing about which
    /// half of the case failed, and the exit code says which.
    /// </remarks>
    private static async Task Substitute(char letter, string target, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunSubst(letter, target, cancellationToken).ConfigureAwait(false);

        Check.Equal(0, exitCode, $"subst maps '{target}' onto {letter}: (it said '{output}')");
    }

    /// <summary>
    /// How long taking the mapping off may take. Short enough that a wedged cleanup fails one case rather
    /// than stalling the run, and independent of the runner's own budget, which by the time a cleanup runs
    /// may already have run out.
    /// </summary>
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Takes the mapping off <paramref name="letter"/> again, without throwing.
    /// </summary>
    /// <remarks>
    /// A failure here is deliberately swallowed: throwing from a <c>finally</c> replaces whatever the case
    /// was actually reporting with the cleanup's own complaint, and the worst a leaked mapping does is
    /// occupy a letter that <see cref="FirstUnusedDriveLetter"/> will then skip.
    /// <para>
    /// It waits on a budget of its own rather than on the case's token, which by now may have been
    /// cancelled — a cleanup that honoured that token would leave the mapping behind instead of removing
    /// it, and one with no bound at all would hang the whole run instead of failing this case.
    /// </para>
    /// </remarks>
    private static async Task RemoveSubstitute(char letter)
    {
        using var budget = new CancellationTokenSource(CleanupBudget);

        try
        {
            await RunSubst(letter, target: null, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Win32Exception or CheckFailedException)
        {
            // The mapping's fate is not this case's result, and reporting it here would replace the
            // failure the case is actually about.
        }
    }

    private static async Task<(int ExitCode, string Output)> RunSubst(
        char letter,
        string? target,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "subst",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add(letter + ":");
        startInfo.ArgumentList.Add(target ?? "/d");

        using var process = Process.Start(startInfo);
        Check.NotNull(process, "subst is on the path and starts");

        // Both streams are read at once, because reading one to the end before the other would stall a
        // child that fills the pipe nobody is reading. The token bounds the wait, so a subst that never
        // returns fails this case instead of hanging the run past the budget the runner sets.
        var standardOutput = process!.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A subst left running after its budget is spent may already have made the mapping, and a
            // mapping outlives the case that asked for it. Killing it here is what keeps the cleanup that
            // follows honest about whether anything is still mounted.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        process.WaitForExit();

        return (process.ExitCode, await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
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
    /// same answer. The Shell does not accept the prefixed form and <see cref="DriveInfo"/> refuses it
    /// outright, so without stripping the answer collapses to Indeterminate for a volume that is
    /// plainly there.
    /// </summary>
    private static async Task ExtendedLengthSpellingOfTheRootIsTheSameVolume(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;
        var plain = await RecycleBin.GetStatusAsync(volumeRoot, context.CancellationToken).ConfigureAwait(false);
        var extended = await RecycleBin.GetStatusAsync(@"\\?\" + volumeRoot, context.CancellationToken).ConfigureAwait(false);

        Check.Equal(plain.VolumeRoot, extended.VolumeRoot, "both spellings name the same volume root");
        Check.Equal(plain.DriveType, extended.DriveType, "and the same drive type, so the volume is not unreadable");
        Check.Equal(plain.Reason, extended.Reason, "and the same reason");

        workspace.Dispose();
    }

    private const string BitBucketVolumeList = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume";

    private static string Escape(string value) => value.Replace("\0", "<NUL>");

    /// <summary>
    /// A caller holding a volume as <c>E:</c> and a caller holding the same volume as <c>E:\</c> are
    /// asking the same question, and have to get the same answer.
    /// </summary>
    /// <remarks>
    /// <c>Path.GetPathRoot("E:")</c> is <c>E:</c>, which is a drive-relative path rather than a root. It
    /// satisfies an equality check against itself while being useless to
    /// <c>GetVolumeNameForVolumeMountPoint</c>, which needs the trailing separator — so the volume GUID
    /// came back unreadable and the answer degraded to Indeterminate for a volume that is plainly there.
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
        Check.Equal(
            RecycleBinAvailabilityReason.Enabled,
            bare.Reason,
            $"a volume that has just accepted a delete is not Indeterminate (got {bare.Reason})");
    }

    /// <summary>
    /// The registry can say one thing while the volume does another, so the answer is checked against
    /// observed behaviour: a volume that accepted a delete into the Recycle Bin cannot be reported as
    /// disabled. This is the test that would catch the policy paths disagreeing with the Shell.
    /// </summary>
    private static async Task StatusAgreesWithAVolumeThatJustAcceptedADelete(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();
        var files = workspace.CreateRandomFiles(1, 4096);
        var volumeRoot = Path.GetPathRoot(ScratchWorkspace.Root)!;

        var recycled = await FileRecycler
            .MoveToRecycleBinAsync(files, cancellationToken: context.CancellationToken)
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
        var status = await RecycleBin.GetStatusAsync("Z:\\", context.CancellationToken).ConfigureAwait(false);

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

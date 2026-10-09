using System.ComponentModel;
using System.Diagnostics;
using RecycleBinToolkit;
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// A <c>subst</c> mapping, which is the only shape of a volume with no Recycle Bin that can be produced
/// on any machine.
/// </summary>
/// <remarks>
/// A substituted drive is a DOS device over a real volume, so it reports a drive type that supports a
/// Recycle Bin, yet it has no GUID for the per-volume setting to be keyed on and no bin for the Shell
/// to query — <c>SHQueryRecycleBin</c> answers <c>0x80070003</c> for it, as it does for a network share.
/// That is what makes it the reproduction of a removable volume or a share that the default run can make.
/// </remarks>
internal static class SubstitutedVolume
{
    /// <summary>
    /// The letter <see cref="FirstUnusedDriveLetter"/> refuses to hand out, so a <c>subst</c> mapping
    /// never takes the letter that means "a volume that is not mounted".
    /// </summary>
    /// <remarks>
    /// The cases that ask about an absent volume get that letter from
    /// <see cref="AbsentVolume.Letter"/>, which asks the machine and does not care which letter comes
    /// back. What still has to be kept off it is the mapping: a mapping left behind on that letter would
    /// turn every absent-volume case into a question about a real volume, and the failures would name
    /// the Recycle Bin rather than the leak that caused them. This reservation is what stops that, and
    /// it costs the pool one letter.
    /// </remarks>
    internal const char AbsentVolumeLetter = 'Z';

    /// <summary>
    /// The highest drive letter nothing is mounted on and no other case has claimed, so a mapping cannot
    /// collide with a real volume, a network drive, or a mapping an earlier case leaked.
    /// </summary>
    /// <remarks>
    /// Scanning down from <c>Z</c> rather than up from <c>D</c> keeps the mapping out of the way of the
    /// fixed drives the caller named, which is whichever volume root <c>--scratch</c> carries and is not
    /// written into this source. A leaked mapping is handled by the scan skipping a letter that is now
    /// mounted, so a leak costs the pool one letter rather than breaking a later case.
    /// </remarks>
    internal static char FirstUnusedDriveLetter()
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
    /// leaves the letter unmounted, and a case would then report
    /// <see cref="RecycleBinAvailabilityReason.VolumeNotFound"/> for a volume it meant to find bin-less.
    /// It cannot pass that way — the required reason is not that one — but it can be confusing about
    /// which half of the case failed, and the exit code says which.
    /// </remarks>
    internal static async Task Substitute(char letter, string target, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunSubst(letter, target, cancellationToken).ConfigureAwait(false);

        Check.Equal(0, exitCode, $"subst maps '{target}' onto {letter}: (it said '{output}')");
    }

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
    internal static async Task RemoveSubstitute(char letter)
    {
        using var budget = new CancellationTokenSource(CleanupBudget);

        try
        {
            await RunSubst(letter, target: null, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or Win32Exception or CheckFailedException)
        {
            // The mapping's fate is not this case's result, and reporting it here would replace the
            // failure the case is actually about.
        }
    }

    /// <summary>
    /// How long taking the mapping off may take. Short enough that a wedged cleanup fails one case rather
    /// than stalling the run, and independent of the runner's own budget, which by the time a cleanup runs
    /// may already have run out.
    /// </summary>
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(30);

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

        // The token bounds the two stream reads, and nothing else. Reading them to the end says the
        // pipes closed, which a child whose work is still going does not do -- so this wait needs its own
        // bound, or a subst that closed its output and then lingered would hold the runner past the
        // budget it sets. Every caller that wants a mapping removed runs it from a finally, and there are
        // fourteen of them across FacadeSuite, StatusSuite and ScratchSuite -- a tenth of the run's cases,
        // but a tenth that hangs on its own if this does. So an unbounded wait here is a hang on the
        // failure path as much as on the passing one.
        if (!process.WaitForExit(ExitTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"subst did not exit within {ExitTimeout}.");
        }

        return (process.ExitCode, await standardOutput.ConfigureAwait(false) + await standardError.ConfigureAwait(false));
    }

    /// <summary>
    /// How long the child gets to exit once its output has been read. Generous for a mapping and short
    /// enough that a wedged one fails its case rather than the run.
    /// </summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);
}
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// A drive letter nothing is mounted on, asked of the machine rather than written into a test.
/// </summary>
/// <remarks>
/// Four cases need a volume that does not exist, and one of them empties it. A letter written into the
/// source is the one letter a machine may well have mounted anyway, and a written-down letter that turns
/// out to be mounted turns a claim about an absent volume into an act against a stranger's Recycle Bin.
/// That is why the letter is looked up rather than written down, and why it is looked up again before the
/// one call that destroys anything.
/// </remarks>
internal static class AbsentVolume
{
    /// <summary>
    /// The highest drive letter this machine has nothing mounted on, <c>'Z'</c> included when nothing is
    /// mounted there.
    /// </summary>
    /// <remarks>
    /// Two answers have to agree before a letter is handed out, and neither one alone settles it.
    /// <see cref="DriveInfo"/> does not enumerate every root Windows will resolve, so a letter it omits
    /// can still resolve to a directory — and a letter that resolves is a real volume, which is exactly
    /// what a case asking about an absent one must not be handed.
    /// <para>
    /// Scanning downwards from <c>'Z'</c> rather than upwards from <c>'A'</c> keeps the answer away from
    /// the fixed volumes the caller named through <c>--scratch</c>, and out of the way of the letters a
    /// <c>subst</c> mapping takes.
    /// </para>
    /// </remarks>
    /// <returns>
    /// A letter between <c>'A'</c> and <c>'Z'</c> that <see cref="DriveInfo"/> does not report and whose
    /// root does not resolve. <c>'Z'</c> itself comes out of this whenever nothing is mounted there —
    /// that is the lookup working, not the source deciding.
    /// </returns>
    /// <exception cref="CheckFailedException">
    /// Every letter from <c>'Z'</c> down to <c>'A'</c> is taken, so no letter is left to hand out.
    /// </exception>
    internal static char Letter()
    {
        var reported = new HashSet<char>(
            DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])));

        for (var letter = 'Z'; letter >= 'A'; letter--)
        {
            if (!reported.Contains(letter) && !Directory.Exists(RootOf(letter)))
            {
                return letter;
            }
        }

        throw new CheckFailedException(
            "every drive letter from Z down to A is mounted on this machine, so there is no absent volume left to ask a question about");
    }

    /// <summary>
    /// The volume root <paramref name="letter"/> names, spelled the way the library and the Shell want it.
    /// </summary>
    /// <param name="letter">A drive letter, as returned by <see cref="Letter"/>.</param>
    /// <returns>The root with its trailing separator.</returns>
    /// <remarks>
    /// The separator is added here rather than left to the caller because a bare <c>X:</c> is
    /// drive-relative: it names whichever directory the process happens to have current for that drive
    /// rather than the volume, which is the same trap <c>VolumeRoot.Normalize</c> refuses.
    /// </remarks>
    internal static string RootOf(char letter) =>
        letter + ":" + Path.DirectorySeparatorChar;

    /// <summary>
    /// Ends the case as not run when <paramref name="letter"/> is mounted after all, so nothing is asked
    /// of a volume this machine has.
    /// </summary>
    /// <remarks>
    /// The letter is checked again here rather than trusted from wherever it was found. The caller that
    /// matters is <c>EmptySuite</c>, which is the one call in this group that destroys what it is pointed
    /// at, and the window between a lookup and the call it precedes is milliseconds — long enough for a
    /// card reader, a mapped network drive or somebody's disk to take the letter. A letter that was
    /// genuinely absent a moment ago says nothing about the letter by the time the empty runs, so the
    /// check sits immediately before the call rather than at the top of the case.
    /// </remarks>
    /// <param name="letter">The letter the case is about to act on.</param>
    /// <param name="because">
    /// What the case was about to do, so the skip names the claim that was not made rather than reporting
    /// a bare absent letter.
    /// </param>
    /// <exception cref="CheckSkippedException">
    /// The letter is mounted now, or its root resolves to a directory <see cref="DriveInfo"/> does not
    /// report. Always thrown, so a caller that does not catch cannot read past it.
    /// </exception>
    internal static void RequireAbsent(char letter, string because)
    {
        var mounted = DriveInfo.GetDrives()
            .FirstOrDefault(drive => char.ToUpperInvariant(drive.Name[0]) == letter);

        if (mounted is not null)
        {
            Check.Skip(
                $"{letter}: is mounted ({mounted}), and {because} would act on that volume rather than on one that is not there");
        }

        if (Directory.Exists(RootOf(letter)))
        {
            Check.Skip(
                $"{RootOf(letter)} resolves to a directory although DriveInfo does not report {letter}:, and {because} " +
                "would act on that volume rather than on one that is not there");
        }
    }
}
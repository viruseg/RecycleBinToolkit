using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Asks the machine which volumes it has, rather than writing drive letters into a case.
/// </summary>
/// <remarks>
/// A letter written into a case is one that goes red on every volume but the one it was written for, and
/// <c>AGENTS.md</c> makes every volume a legal answer to <c>--scratch</c>: a removable drive and a share
/// are both named there as first-class choices. A run on either must be green or skipped and never red for
/// want of the right kind of disk.
/// <para>
/// These are judgments about the machine and not about the operation that needs them, so they live here
/// rather than in whichever suite happened to need them first.
/// </para>
/// </remarks>
internal static class VolumePresence
{
    /// <summary>
    /// Every named letter names a volume on this machine, or the case is skipped.
    /// </summary>
    /// <param name="letters">Drive letters the case needs, read only as letters — nothing is created.</param>
    /// <exception cref="CheckSkippedException">
    /// One of the letters is not a volume on this machine.
    /// </exception>
    /// <remarks>
    /// The volumes these cases compare are named in the source, so what they need is a fact about the
    /// machine rather than anything the run can arrange — and a case that read a machine's partition layout
    /// as a defect would be the repository's rule against asking a run to be the right kind of disk, with the
    /// letters as the "right kind".
    /// <para>
    /// Presence is read from the root resolving rather than from <see cref="DriveInfo"/> alone, for the
    /// reason <see cref="AbsentVolume.RequireAbsent"/> checks both ways: an enumeration omits roots Windows
    /// still resolves, and a root that resolves is a volume whichever way the enumeration reads.
    /// </para>
    /// </remarks>
    internal static void Require(params char[] letters)
    {
        ArgumentNullException.ThrowIfNull(letters);

        foreach (var letter in letters)
        {
            var root = AbsentVolume.RootOf(letter);

            if (!Directory.Exists(root))
            {
                Check.Skip(
                    $"{root} is not a volume on this machine. This case needs {string.Join(", ", letters)} all present, "
                    + "to read which physical disk each one sits on");
            }
        }
    }

    /// <summary>
    /// A letter naming a second volume on the same physical disk as the one <c>--scratch</c> named, or a skip.
    /// </summary>
    /// <param name="letter">Receives that letter, when this returns.</param>
    /// <param name="because">
    /// What the case needed a second volume for, as a noun phrase the skip line can carry: it completes
    /// "This case needs ___".
    /// </param>
    /// <exception cref="CheckSkippedException">
    /// The run's volume named no physical disk, or nothing else on this machine shares one with it.
    /// </exception>
    /// <remarks>
    /// Asked of the machine rather than written into the source, for the reason <see cref="Require"/> gives.
    /// <para>
    /// The run's own letter is excluded by comparison rather than by arithmetic: <c>A:</c> has no
    /// <c>Z:</c> beside it, and a run on <c>Z:</c> has no <c>Y:</c> behind it either. What the two must
    /// share is a <see cref="PhysicalDiskKey"/>, so a machine with one volume per disk — and a run on a
    /// share, which names no disk at all — is skipped rather than answered with a second volume that would
    /// put the two in different groups and quietly change what the case is about.
    /// </para>
    /// <para>
    /// The mirror of <see cref="RequireADifferentDisk"/>, and deliberately beside it: a group needs two
    /// members to mean anything, and two groups need two disks, so both questions are the same question with
    /// the comparison flipped.
    /// </para>
    /// </remarks>
    internal static void RequireASameDisk(out char letter, string because)
    {
        letter = default;

        if (!PhysicalVolumeDisk.TryIdentify(ScratchWorkspace.VolumeRoot, out var disk))
        {
            Check.Skip(
                $"{ScratchWorkspace.VolumeRoot} named no physical disk, so no second volume can be asked whether "
                + $"it shares one. This case needs {because}");
        }

        // The run's own letter, read off the root rather than taken from --scratch on the command line.
        // A share answers the branch above, so what reaches here is always a drive root.
        var own = char.ToUpperInvariant(ScratchWorkspace.VolumeRoot[0]);

        foreach (var drive in DriveInfo.GetDrives())
        {
            var name = drive.Name;

            if (name.Length < 2 || !char.IsLetter(name[0]) || char.ToUpperInvariant(name[0]) == own)
            {
                continue;
            }

            var candidate = char.ToUpperInvariant(name[0]);

            if (PhysicalVolumeDisk.TryIdentify(AbsentVolume.RootOf(candidate), out var found) && found == disk)
            {
                letter = candidate;

                return;
            }
        }

        Check.Skip(
            $"{ScratchWorkspace.VolumeRoot} is the only volume on its physical disk on this machine, so there is "
            + $"no second one for a group to hold. This case needs {because}");
    }

    /// <summary>
    /// A letter naming a volume on a physical disk other than the one <c>--scratch</c> named, or a skip.
    /// </summary>
    /// <param name="letter">Receives that letter, when this returns.</param>
    /// <param name="because">
    /// What the case needed a second disk for, as a noun phrase the skip line can carry: it completes
    /// "This case needs ___".
    /// </param>
    /// <exception cref="CheckSkippedException">
    /// The run's volume named no physical disk, or nothing else on this machine sits on another one.
    /// </exception>
    /// <remarks>
    /// Asked of the machine rather than written into the source, for the reason <see cref="Require"/> gives.
    /// <para>
    /// The run's own letter is excluded by comparison rather than by arithmetic, for the reason
    /// <see cref="RequireASameDisk"/> gives.
    /// </para>
    /// <para>
    /// What the two must <em>not</em> share is a <see cref="PhysicalDiskKey"/>. A machine with one physical
    /// disk, and a run on a share, which names no disk, are both skipped rather than answered with a second
    /// volume that would put the two in one group and quietly change what the case is about — the same
    /// failure in the opposite direction, and the reason both guards live here together.
    /// </para>
    /// </remarks>
    internal static void RequireADifferentDisk(out char letter, string because)
    {
        letter = default;

        if (!PhysicalVolumeDisk.TryIdentify(ScratchWorkspace.VolumeRoot, out var disk))
        {
            Check.Skip(
                $"{ScratchWorkspace.VolumeRoot} named no physical disk, so no other volume can be asked whether "
                + $"it sits on one. This case needs {because}");
        }

        // The run's own letter, read off the root rather than taken from --scratch on the command line.
        // A share answers the branch above, so what reaches here is always a drive root.
        var own = char.ToUpperInvariant(ScratchWorkspace.VolumeRoot[0]);

        foreach (var drive in DriveInfo.GetDrives())
        {
            var name = drive.Name;

            if (name.Length < 2 || !char.IsLetter(name[0]) || char.ToUpperInvariant(name[0]) == own)
            {
                continue;
            }

            var candidate = char.ToUpperInvariant(name[0]);

            if (PhysicalVolumeDisk.TryIdentify(AbsentVolume.RootOf(candidate), out var found) && found != disk)
            {
                letter = candidate;

                return;
            }
        }

        Check.Skip(
            $"{ScratchWorkspace.VolumeRoot} shares its physical disk with every other volume on this machine, so "
            + $"there is no second disk for a second group. This case needs {because}");
    }
}
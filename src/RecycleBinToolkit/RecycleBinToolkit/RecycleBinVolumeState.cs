using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the facts about one volume and turns them into a <see cref="RecycleBinStatus"/>.
/// </summary>
/// <remarks>
/// Reads what the volume table and the registry say. A volume whose per-volume setting was not
/// recorded — one whose GUID the volume API would not resolve, or one Windows has never configured
/// a bin for — is answered <see cref="RecycleBinAvailabilityReason.Indeterminate"/>: unproven is not
/// enabled.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RecycleBinVolumeState
{
    private const int GuidPathCapacity = 64;

    /// <summary>
    /// Win32 <c>ERROR_NOT_SET</c>, used when a volume cannot be read at all.
    /// </summary>
    internal const int UnreadableVolume = unchecked((int)0x80070490);

    /// <summary>
    /// Inspects a volume.
    /// </summary>
    /// <param name="volumeRoot">Volume root, with or without a trailing separator.</param>
    /// <returns>The status of that volume.</returns>
    /// <remarks>
    /// Configuration is the evidence throughout, which is also its limit: a key that survives on a volume
    /// whose bin has since been removed still answers <see cref="RecycleBinAvailabilityReason.Enabled"/>.
    /// And a volume with no recorded setting answers
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/> rather than a statement about whether a
    /// bin directory exists — nothing here walks the bin to check.
    /// </remarks>
    internal static RecycleBinStatus Inspect(string volumeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        var root = NormalizeVolumeRoot(volumeRoot);

        // A path that is not a volume root is not a question about a volume, and a drive letter that
        // is not mounted has no volume table entry behind it. Both are answered before anything else
        // so a caller never reads them as a Recycle Bin that happens to be off.
        if (string.IsNullOrEmpty(Path.GetPathRoot(volumeRoot)) || !IsVolumeRoot(volumeRoot) || !Directory.Exists(root))
        {
            return new RecycleBinStatus(root, DriveType.Unknown, RecycleBinAvailabilityReason.VolumeNotFound);
        }

        var driveType = ReadDriveType(root);
        var disabledByPolicy = RecycleBinRegistry.IsDisabledByPolicy();

        // The per-volume setting lives under a subkey named after the volume's GUID, so both a volume
        // whose GUID cannot be read and a volume whose key Windows never wrote come back unknown, and
        // unknown is not the same thing as recycles.
        var setting = RecycleBinRegistry.ReadNukeOnDelete(TryGetVolumeGuidKeyName(root));
        var configured = RecycleBinVolumePolicy.Classify(
            volumeExists: true,
            driveType,
            disabledByPolicy,
            setting is RecycleBinVolumePolicy.VolumeRecycleSetting.Nukes);

        // A policy, a volume type that never had a bin, and a volume set to delete outright are all
        // reasons the caller can act on, and each of them is settled above.
        if (configured is not (RecycleBinAvailabilityReason.Enabled or RecycleBinAvailabilityReason.Indeterminate))
        {
            return new RecycleBinStatus(root, driveType, configured);
        }

        // Windows recorded this volume as recycling, so the answer is that reading and nothing has to be
        // asked.
        if (setting is RecycleBinVolumePolicy.VolumeRecycleSetting.Recycled)
        {
            return new RecycleBinStatus(root, driveType, configured);
        }

        // Nothing was recorded for this volume, which is not a fact about whether it recycles.
        // Unproven is not enabled: the answer is Indeterminate, and the default action refuses it
        // with the file left in place.
        return new RecycleBinStatus(root, driveType, RecycleBinAvailabilityReason.Indeterminate);
    }

    /// <summary>
    /// Whether the path names a network share, decided from the path itself rather than by asking the
    /// machine. Answers for a path inside a share as well as for the share root.
    /// </summary>
    /// <remarks>
    /// The extended-length prefix comes off first: <c>\\?\UNC\server\share</c> names a share as plainly
    /// as <c>\\server\share</c> does, and reading it for its first characters alone would answer no.
    /// <para>
    /// This is asked instead of <see cref="DriveInfo"/> because <see cref="DriveInfo"/> throws
    /// <see cref="ArgumentException"/> on a UNC path rather than reporting
    /// <see cref="DriveType.Network"/>. The type then reads <see cref="DriveType.Unknown"/> and the
    /// volume is reported <see cref="RecycleBinAvailabilityReason.Indeterminate"/>, which is a guess
    /// about a location whose type the path already states.
    /// </para>
    /// </remarks>
    /// <param name="root">A path or volume root, in any spelling.</param>
    /// <returns>
    /// <see langword="true"/> for a UNC path, including the extended-length spelling; otherwise
    /// <see langword="false"/>. A share written with forward slashes is not recognised — see
    /// <see cref="RootForm(string)"/>.
    /// </returns>
    internal static bool IsUncRoot(string root) =>
        RecycleTarget.StripExtendedPrefix(root).StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Whether the path names a volume root rather than something inside one. A path is told from its
    /// root by what follows it, not by how the root happens to be spelled.
    /// </summary>
    /// <remarks>
    /// The two sides are compared in a reduced form rather than as the caller typed them, because
    /// <see cref="Path.GetPathRoot(string)"/> does not spell a root the way its caller did:
    /// <c>\\server\share</c> is the root of both <c>\\server\share</c> and <c>\\server\share\</c>, and
    /// the root it derives from <c>\\?\UNC\server\share</c> still carries the prefix. Comparing either
    /// pair verbatim calls one share two volumes, and the caller gets two answers to one question.
    /// <para>
    /// The reduction drops the extended-length prefix and the trailing separator, but nothing else — a
    /// path that goes <em>past</em> the root has to stay distinguishable from the root. Comparing whole
    /// roots instead would answer that <c>X:\work\</c> is the same volume as <c>X:\</c>.
    /// </para>
    /// <para>
    /// An empty path is refused before that comparison, because it is the one input with no root to
    /// reduce: <see cref="Path.GetPathRoot(string)"/> answers nothing for it, and an absent answer on both
    /// sides would agree. Left alone, a caller passing an empty string gets a root of <c>"\"</c>,
    /// which is not a volume at all.
    /// </para>
    /// </remarks>
    internal static bool IsVolumeRoot(string volumeRoot) =>
        !string.IsNullOrEmpty(volumeRoot) &&
        string.Equals(RootForm(volumeRoot), RootForm(Path.GetPathRoot(volumeRoot)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One spelling of a root reduced to the form the spellings callers and Windows write share: no
    /// extended-length prefix, no trailing separator.
    /// </summary>
    /// <remarks>
    /// The scope is those spellings and no wider. <c>Path</c> answers a share written with forward
    /// slashes in backslashes, so <c>//server/share</c> is not reduced to the same form as
    /// <c>\\server\share</c> and is not read as a root — which is also how it read before this reduction
    /// existed.
    /// <para>
    /// A path with no root at all reduces to the empty string, which is what
    /// <see cref="IsVolumeRoot"/> then refuses rather than comparing.
    /// </para>
    /// </remarks>
    private static string RootForm(string? path) =>
        string.IsNullOrEmpty(path)
            ? string.Empty
            : RecycleTarget.StripExtendedPrefix(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static DriveType ReadDriveType(string root)
    {
        // A share is asked about its own path, because DriveInfo throws on one rather than answering.
        if (IsUncRoot(root))
        {
            return DriveType.Network;
        }

        try
        {
            return new DriveInfo(root).DriveType;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return DriveType.Unknown;
        }
    }

    /// <summary>
    /// Reads the name the per-volume Recycle Bin setting is keyed on: the volume's GUID in braces,
    /// which is what the subkey under the per-volume list is called.
    /// </summary>
    /// <remarks>
    /// Not the <c>\\?\Volume{…}\</c> device path the volume API answers with. That path is how Win32
    /// names the volume; the registry calls the same volume by the bare GUID in braces, and building a
    /// subkey name out of the device path misses every time — which reports a volume whose recycling is
    /// switched off as enabled.
    /// </remarks>
    /// <returns>The braced GUID, or <see langword="null"/> when it could not be read.</returns>
    internal static string? TryGetVolumeGuidKeyName(string root)
    {
        var buffer = new char[GuidPathCapacity];

        if (!PInvoke.GetVolumeNameForVolumeMountPoint(root, buffer))
        {
            return null;
        }

        // The volume API null-terminates its answer and leaves the rest of the buffer alone, so the name
        // ends at the first terminator rather than at the end of the array. Reading to the end instead
        // puts fifteen NUL characters into every subkey name built from it.
        var written = Array.IndexOf(buffer, '\0');
        if (written < 0)
        {
            return null;
        }

        var path = buffer.AsSpan(0, written);
        var open = path.IndexOf('{');
        var close = path.IndexOf('}');

        if (open < 0 || close < open)
        {
            return null;
        }

        return path[open..(close + 1)].ToString();
    }

    /// <summary>
    /// The volume a path belongs to: a root with a trailing separator and no extended-length prefix,
    /// in the form the volume APIs accept.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetPathRoot(string)"/> answers <c>X:</c> for a bare drive letter, which is a
    /// drive-relative path rather than a root and is rejected by
    /// <c>GetVolumeNameForVolumeMountPoint</c>. Collapsing to a root alone is therefore not enough: the
    /// separator has to be added back.
    /// <para>
    /// A UNC path needs both corrections more than a drive letter does, because <c>Path</c> spells it
    /// differently from the way callers write it: it answers <c>\\server\share</c> for a share whether or
    /// not the caller typed the trailing separator, and it hands back <c>\\?\UNC\server\share</c> with
    /// the extended-length prefix still attached. <see cref="RecycleTarget.StripExtendedPrefix"/> turns
    /// the second back into <c>\\server\share</c>, and the separator this method adds is what makes every
    /// spelling of one share the same string.
    /// </para>
    /// <para>
    /// The extended-length prefix comes off for the same reason it comes off everywhere else here:
    /// <see cref="DriveInfo"/> throws on <c>\\?\X:\</c>, so a prefixed root is handed back as a volume
    /// whose type could not be read and is reported Indeterminate rather than as the fact it is.
    /// Reducing every spelling of a root to one form is what makes the answer independent of spelling.
    /// </para>
    /// </remarks>
    /// <param name="volumeRoot">A volume root or any path inside one.</param>
    /// <returns>
    /// The root with a trailing separator and no prefix — <c>X:\</c>, or <c>\\server\share\</c> for a
    /// share in any of its spellings.
    /// </returns>
    internal static string NormalizeVolumeRoot(string volumeRoot)
    {
        var root = Path.GetPathRoot(volumeRoot);
        if (string.IsNullOrEmpty(root))
        {
            return AppendSeparator(RecycleTarget.StripExtendedPrefix(volumeRoot));
        }

        return AppendSeparator(RecycleTarget.StripExtendedPrefix(root));
    }

    private static string AppendSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}

using System.Runtime.Versioning;
using System.Security.Principal;

namespace RecycleBinToolkit;

/// <summary>
/// Where the native backend keeps things: which directory holds a volume's bin, and what the two halves
/// of one entry are called.
/// </summary>
/// <remarks>
/// A Recycle Bin is <c>&lt;volume&gt;$RECYCLE.BIN\&lt;SID&gt;\</c>, and each entry inside it is a pair:
/// a <c>$I</c> record naming what was deleted and where from, and the <c>$R</c> payload that was. Only
/// one entry's index is read to learn about it, and the payload's name is derived from it rather than
/// looked up.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class NativeBinLayout
{
    private const string RecycleBinFolder = "$RECYCLE.BIN";
    private const char IndexPrefix = 'I';
    private const char PayloadPrefix = 'R';

    /// <summary>
    /// Characters an entry's random part is built from. Windows uses the same set, and matching it means
    /// the entries read as ordinary ones rather than as something unfamiliar.
    /// </summary>
    private const string StemAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>
    /// How many characters the random part is. Windows uses six, so an entry written here is the same
    /// shape as one it wrote.
    /// </summary>
    internal const int StemLength = 6;

    /// <summary>
    /// The SID of the user this process runs as, which names the one bin directory this library reads and
    /// writes. The same one the Shell folder shows an unelevated caller.
    /// </summary>
    /// <remarks>
    /// Asked fresh each time rather than remembered for the life of the process. A host that impersonates
    /// — IIS, a pooled application switching identity between requests — would otherwise pin every later
    /// request to whichever identity happened to be current the first time, and write one user's files into
    /// another user's bin. It is asked once per volume per operation rather than once per path, which
    /// makes the cost one handle per volume and not one per file.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The process has no Windows identity, so there is no Recycle Bin that belongs to it.
    /// </exception>
    internal static string CurrentSid =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException(
            "This process has no Windows identity, so there is no Recycle Bin to write to. The Shell reads a " +
            "per-user directory and a process without an identity belongs to none.");

    /// <summary>
    /// The bin directory of a volume, composed without touching the filesystem.
    /// </summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\\"</c>.</param>
    /// <returns>The directory this user's entries live in on that volume.</returns>
    internal static string BinDirectory(string volumeRoot) =>
        Path.Combine(volumeRoot, RecycleBinFolder, CurrentSid);

    /// <summary>
    /// The bin directory of a volume, created if nothing has ever been recycled there.
    /// </summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\\"</c>.</param>
    /// <returns>The directory this user's entries live in on that volume.</returns>
    /// <remarks>
    /// Creating it is what the Shell does on the first delete of a session, and doing it here is what lets
    /// the very first recycle on a volume succeed. The directory is per user, so this touches nothing any
    /// other account on the machine can see.
    /// </remarks>
    internal static string EnsureBinDirectory(string volumeRoot)
    {
        var directory = BinDirectory(volumeRoot);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// The random part an entry two names share.
    /// </summary>
    /// <param name="destination">Receives <see cref="StemLength"/> characters from the alphabet Windows uses.</param>
    /// <remarks>
    /// Nothing is probed, and that is the whole point. Both names this becomes are refused by the
    /// filesystem if they are taken — the record is created with <see cref="FileMode.CreateNew"/>, which
    /// fails rather than overwriting, and a move onto a name that exists fails rather than replacing it.
    /// A payload left behind by a crash with no record of its own therefore cannot be overwritten and
    /// cannot be silently joined to the wrong record; it costs the caller one path that did not move, and
    /// the caller gives that path a fresh name.
    /// <para>
    /// Asking first would be a filesystem round trip on every path in the batch to avoid that, against a
    /// collision of about one name in thirty-six to the sixth — two in a billion. That trade is not close.
    /// </para>
    /// </remarks>
    internal static void NextStem(Span<char> destination) => NextStemInto(destination);

    /// <summary>
    /// The path a record of the given stem is to be written to.
    /// </summary>
    internal static string IndexPath(string binDirectory, string stem, string extension) =>
        Path.Combine(binDirectory, $"${IndexPrefix}{stem}{extension}");

    /// <summary>
    /// The path the payload of the given stem is to be moved to.
    /// </summary>
    internal static string PayloadPath(string binDirectory, string stem, string extension) =>
        Path.Combine(binDirectory, $"${PayloadPrefix}{stem}{extension}");

    /// <summary>
    /// The payload path belonging to an index path.
    /// </summary>
    /// <param name="indexPath">Path of an <c>$I</c> record.</param>
    /// <returns>The <c>$R</c> path beside it, worked out from the name and not looked up.</returns>
    /// <remarks>
    /// The two names differ in their first character and in nothing else, so the payload is a spelling of
    /// the record rather than a directory entry that has to be found. On a bin of ninety thousand entries
    /// that is ninety thousand lookups this does not perform.
    /// </remarks>
    internal static string ToPayloadPath(string indexPath)
    {
        var name = Path.GetFileName(indexPath);

        if (name.Length < 2 || name[0] != '$' || (name[1] != IndexPrefix && name[1] != char.ToLowerInvariant(IndexPrefix)))
        {
            return indexPath;
        }

        return Path.Combine(
            Path.GetDirectoryName(indexPath) ?? string.Empty,
            $"${PayloadPrefix}{name[2..]}");
    }

    private static void NextStemInto(Span<char> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = StemAlphabet[Random.Shared.Next(StemAlphabet.Length)];
        }
    }
}
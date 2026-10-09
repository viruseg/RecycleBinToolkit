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
    private const int StemLength = 6;

    /// <summary>
    /// How many candidates to try before giving up on a free name. Thirty-six to the sixth is about two
    /// billion, so exhausting thirty-six attempts is not a collision — it is a broken directory.
    /// </summary>
    private const int StemAttempts = 36;

    private static readonly Lazy<string> LazyCurrentSid = new(() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException(
            "This process has no Windows identity, so there is no Recycle Bin to write to. The Shell reads a " +
            "per-user directory and a process without an identity belongs to none."));

    /// <summary>
    /// The SID of the user this process runs as, which names the one bin directory this library reads and
    /// writes. The same one the Shell folder shows an unelevated caller.
    /// </summary>
    /// <remarks>
    /// Read once per process. <see cref="WindowsIdentity.GetCurrent()"/> is not free, and the answer
    /// cannot change while the process lives.
    /// </remarks>
    internal static string CurrentSid => LazyCurrentSid.Value;

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
    /// A pair of names nothing is using yet, one for the record and one for the payload.
    /// </summary>
    /// <param name="binDirectory">The bin directory to look in.</param>
    /// <param name="extension">
    /// The deleted item's own extension, including the dot, or an empty string for one without.
    /// </param>
    /// <param name="indexPath">Receives the path the record is to be written to.</param>
    /// <param name="payloadPath">Receives the path the payload is to be moved to.</param>
    /// <returns><see langword="true"/> when a free pair was found.</returns>
    /// <remarks>
    /// Both names are checked, not just the record's. A payload left behind by a crash carries no record,
    /// so a generator that only avoided existing records would eventually move a new item onto one and
    /// destroy it.
    /// </remarks>
    internal static bool TryCreatePair(
        string binDirectory,
        string extension,
        out string indexPath,
        out string payloadPath)
    {
        // One buffer for every attempt: a stack allocation inside the loop would not be released until the
        // method returns, which on a directory that somehow has thirty-six taken names would be thirty-six
        // live buffers for no reason.
        Span<char> stem = stackalloc char[StemLength];

        for (var attempt = 0; attempt < StemAttempts; attempt++)
        {
            NextStemInto(stem);

            indexPath = Path.Combine(binDirectory, $"${IndexPrefix}{stem}{extension}");
            payloadPath = Path.Combine(binDirectory, $"${PayloadPrefix}{stem}{extension}");

            if (!File.Exists(indexPath) && !File.Exists(payloadPath) && !Directory.Exists(payloadPath))
            {
                return true;
            }
        }

        indexPath = string.Empty;
        payloadPath = string.Empty;
        return false;
    }

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
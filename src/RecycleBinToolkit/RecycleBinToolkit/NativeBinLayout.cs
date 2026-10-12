using System.Runtime.Versioning;
using System.Security.Principal;

namespace RecycleBinToolkit;

/// <summary>
/// Where things are kept: which directory holds a volume's bin, and what the two halves
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

    /// <summary>The character every entry name starts with, before the <c>I</c> or <c>R</c> that says which half it is.</summary>
    private const char EntryMarker = '$';

    private const char IndexPrefix = 'I';
    private const char PayloadPrefix = 'R';

    /// <summary>
    /// Characters an entry's random part is built from. Windows uses the same set, and matching it means
    /// the entries read as ordinary ones rather than as something unfamiliar.
    /// </summary>
    private const string StemAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>
    /// The longest entry name assembled on the stack rather than on the heap. A name is a separator, the
    /// marker, a prefix, six random characters and the extension the caller had, and no filesystem on
    /// Windows gives a name component more than 255 characters — so this is the whole of what a real path
    /// reaches.
    /// </summary>
    private const int MaxStackNameLength = 3 + StemLength + 255;

    /// <summary>
    /// How many characters the random part is. Windows uses six, so an entry written here is the same
    /// shape as one it wrote.
    /// </summary>
    internal const int StemLength = 6;

    /// <summary>
    /// How many distinct stems <see cref="StemLength"/> characters over <see cref="StemAlphabet"/> can
    /// spell: thirty-six to the sixth, which is 2,176,782,336. The space is what makes an entry's name
    /// identifiable without reading its record, and it is the reason
    /// <see cref="BinStemSequencer"/> can run out of indices only on a bin no caller has.
    /// </summary>
    internal const long StemSpace = 2176782336;

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
    /// Six characters drawn at random from the alphabet Windows uses.
    /// </summary>
    /// <param name="destination">Receives <see cref="StemLength"/> characters from the alphabet Windows uses.</param>
    /// <remarks>
    /// <b>The fallback, not the ordinary answer.</b> <see cref="BinStemSequencer"/> hands out names that
    /// climb, because a directory's index is ordered and appending at its right edge keeps every write of a
    /// batch in the same leaf instead of scattering them. This is what that sequencer falls back on: a bin
    /// directory it could not walk, and a stem space it has used up. Neither is a shape an ordinary delete
    /// is in.
    /// <para>
    /// What the two paths share is that nothing is probed: both names this becomes are refused by the
    /// filesystem if they are taken, so a payload left behind by a crash with no record of its own cannot be
    /// overwritten nor silently joined to the wrong record.
    /// </para>
    /// </remarks>
    internal static void NextStem(Span<char> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = StemAlphabet[Random.Shared.Next(StemAlphabet.Length)];
        }
    }

    /// <summary>
    /// Writes the stem that names <paramref name="index"/>, most significant character first and padded with
    /// leading zeros so every stem is the same length.
    /// </summary>
    /// <param name="destination">Receives exactly <see cref="StemLength"/> characters.</param>
    /// <param name="index">
    /// Which stem, from zero. Anything below <see cref="StemSpace"/> is spellable in
    /// <see cref="StemLength"/> characters; a caller holding a larger one has run out of the space and is
    /// asked to fall back to <see cref="NextStem"/> instead.
    /// </param>
    internal static void FormatStem(Span<char> destination, long index)
    {
        for (var i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = StemAlphabet[(int)(index % StemAlphabet.Length)];
            index /= StemAlphabet.Length;
        }
    }

    /// <summary>
    /// Reads back what <see cref="FormatStem"/> wrote, which is what lets one name be compared with
    /// another without a second field to carry the comparison in.
    /// </summary>
    /// <param name="stem">The characters that follow an entry's marker and half-letter.</param>
    /// <param name="index">Receives which stem it is.</param>
    /// <returns>
    /// <see langword="false"/> when any character is outside <see cref="StemAlphabet"/>, which is the
    /// answer for a name that is not one of this format's rather than an exception — the caller is walking
    /// a directory that holds whatever else was ever put in it, and one foreign name is not a reason to
    /// stop.
    /// </returns>
    /// <remarks>
    /// Both cases are accepted, because a directory index is case-insensitive and a name is not
    /// obliged to arrive in the case it was created in.
    /// </remarks>
    internal static bool TryParseStem(ReadOnlySpan<char> stem, out long index)
    {
        long value = 0;

        foreach (var character in stem)
        {
            var digit = DigitValue(character);

            if (digit < 0)
            {
                index = 0;
                return false;
            }

            value = (value * StemAlphabet.Length) + digit;
        }

        index = value;
        return true;
    }

    /// <summary>
    /// Where a character sits in <see cref="StemAlphabet"/>, or <c>-1</c> for a character that is not in it.
    /// </summary>
    private static int DigitValue(char character) => character switch
    {
        >= 'A' and <= 'Z' => character - 'A',
        >= 'a' and <= 'z' => character - 'a',
        >= '0' and <= '9' => (character - '0') + 26,
        _ => -1,
    };

    /// <summary>
    /// The path a record of the given stem is to be written to.
    /// </summary>
    /// <param name="binDirectory">The volume's bin directory for this user.</param>
    /// <param name="stem">The random part, of length <see cref="StemLength"/>.</param>
    /// <param name="extension">The extension the entry keeps, which may be empty.</param>
    internal static string IndexPath(string binDirectory, ReadOnlySpan<char> stem, ReadOnlySpan<char> extension) =>
        EntryPath(binDirectory, IndexPrefix, stem, extension);

    /// <summary>
    /// The path the payload of the given stem is to be moved to.
    /// </summary>
    /// <param name="binDirectory">The volume's bin directory for this user.</param>
    /// <param name="stem">The random part, of length <see cref="StemLength"/>.</param>
    /// <param name="extension">The extension the entry keeps, which may be empty.</param>
    internal static string PayloadPath(string binDirectory, ReadOnlySpan<char> stem, ReadOnlySpan<char> extension) =>
        EntryPath(binDirectory, PayloadPrefix, stem, extension);

    /// <summary>
    /// Builds one half of an entry's path and materialises it once.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/> has no overload taking a span for its second half, so
    /// naming the entry as a string first costs a second allocation on every path of every batch. The
    /// separator that call would have inserted is inserted here instead and the whole path is concatenated
    /// from two spans, which allocates once. Both halves of the assumption are safe: the directory is this
    /// library's own <c>&lt;root&gt;$RECYCLE.BIN\&lt;SID&gt;</c>, and the name is a prefix, a fixed-length
    /// stem and an extension — never rooted, so the "second half wins outright" rule cannot apply.
    /// </remarks>
    private static string EntryPath(string binDirectory, char prefix, ReadOnlySpan<char> stem, ReadOnlySpan<char> extension)
    {
        var separator = binDirectory.Length > 0
            && binDirectory[^1] != Path.DirectorySeparatorChar
            && binDirectory[^1] != Path.AltDirectorySeparatorChar;

        var nameLength = (separator ? 1 : 0) + 2 + stem.Length + extension.Length;

        Span<char> name = nameLength <= MaxStackNameLength
            ? stackalloc char[nameLength]
            : new char[nameLength];

        var at = 0;

        if (separator)
        {
            name[at++] = Path.DirectorySeparatorChar;
        }

        // The dollar is part of the entry's name and not part of the prefix: what marks a file as this
        // format's is the '$', and I and R only say which half it is.
        name[at++] = EntryMarker;
        name[at++] = prefix;
        stem.CopyTo(name[at..]);
        at += stem.Length;
        extension.CopyTo(name[at..]);

        return string.Concat(binDirectory.AsSpan(), (ReadOnlySpan<char>)name);
    }

    /// <summary>
    /// The payload path belonging to an index path.
    /// </summary>
    /// <param name="indexPath">Path of an <c>$I</c> record.</param>
    /// <returns>
    /// The <c>$R</c> path beside it, worked out from the name and not looked up. A path that is not a
    /// record's is returned unchanged, so a caller that hands this something else gets that something back
    /// rather than a mangled version of it.
    /// </returns>
    /// <remarks>
    /// The two names differ in their second character and in nothing else, so the payload is a spelling of
    /// the record rather than a directory entry that has to be found. On a bin of ninety thousand entries
    /// that is ninety thousand lookups this does not perform.
    /// <para>
    /// Built in one allocation rather than five. This runs on every record of every listing and every empty,
    /// which on a full bin is a hundred thousand calls, and the path API's own helpers are what cost: the
    /// name, a slice of it, an interpolated string, the directory and the combine. The name now comes back
    /// as a span and the result is concatenated from three of them.
    /// </para>
    /// <para>
    /// <see cref="Path.GetDirectoryName(string?)"/> is the one call left in place, and it is there on
    /// purpose: it is what turns the forward slashes of a share into backslashes, so replacing it with a
    /// slice of the input would keep the caller's spelling and answer a different question on a path written
    /// with them.
    /// </para>
    /// </remarks>
    internal static string ToPayloadPath(string indexPath)
    {
        ArgumentNullException.ThrowIfNull(indexPath);

        var name = Path.GetFileName(indexPath.AsSpan());

        if (name.Length < 2 ||
            name[0] != EntryMarker ||
            (name[1] != IndexPrefix && name[1] != char.ToLowerInvariant(IndexPrefix)))
        {
            return indexPath;
        }

        var directory = Path.GetDirectoryName(indexPath) ?? string.Empty;

        // Path.Combine's own rule, which is what the call this replaced applied: a separator only where the
        // directory does not already end in one, and none at all where there is no directory.
        var separator = directory.Length > 0
            && directory[^1] != Path.DirectorySeparatorChar
            && directory[^1] != Path.AltDirectorySeparatorChar;

        Span<char> head = stackalloc char[2 + (separator ? 1 : 0)];

        var at = 0;

        if (separator)
        {
            head[at++] = Path.DirectorySeparatorChar;
        }

        head[at++] = EntryMarker;
        head[at++] = PayloadPrefix;

        return string.Concat(directory, head, name[2..]);
    }

    /// <summary>
    /// The record path belonging to a payload path.
    /// </summary>
    /// <param name="payloadPath">Path of an <c>$R</c> payload.</param>
    /// <returns>
    /// The <c>$I</c> path beside it, worked out from the name and not looked up. A path that is not a
    /// payload's is returned unchanged, so a caller that hands this something else gets that something back
    /// rather than a mangled version of it.
    /// </returns>
    /// <remarks>
    /// The exact inverse of <see cref="ToPayloadPath"/>, and free of the lookup that direction costs a caller:
    /// a listing hands out the payload path of every entry, and this turns it into the record that describes
    /// it without walking the bin directory to find which record that is.
    /// </remarks>
    internal static string ToIndexPath(string payloadPath)
    {
        ArgumentNullException.ThrowIfNull(payloadPath);

        var name = Path.GetFileName(payloadPath.AsSpan());

        if (name.Length < 2 ||
            name[0] != EntryMarker ||
            (name[1] != PayloadPrefix && name[1] != char.ToLowerInvariant(PayloadPrefix)))
        {
            return payloadPath;
        }

        var directory = Path.GetDirectoryName(payloadPath) ?? string.Empty;

        var separator = directory.Length > 0
            && directory[^1] != Path.DirectorySeparatorChar
            && directory[^1] != Path.AltDirectorySeparatorChar;

        Span<char> head = stackalloc char[2 + (separator ? 1 : 0)];

        var at = 0;

        if (separator)
        {
            head[at++] = Path.DirectorySeparatorChar;
        }

        head[at++] = EntryMarker;
        head[at++] = IndexPrefix;

        return string.Concat(directory, head, name[2..]);
    }
}
using System.IO.Enumeration;
using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Hands out entry names for one bin directory in an order that keeps writing to it in one place.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A bin directory of a few thousand entries is a B-tree of index pages, and a name drawn at random is
/// inserted wherever its sort order falls, so every write of a batch lands in a different leaf. Names that
/// climb are appended at the right edge instead.</item>
/// <item>The set is keyed on the stem, not on the name minus its marker: <c>$I000000.txt</c> and
/// <c>$I000000.bin</c> are two names a filesystem would hold at once, and keying on the part without the
/// extension would hand one index to two files and put one write at each end of the index again.</item>
/// <item>This is not a check on whether a name is free. The walk sees a bin as it was when it looked, and
/// what stops a name slipping in is this library's own <see cref="FileMode.CreateNew"/> on the record,
/// which is unchanged, with <see cref="NameCollision.IsTaken"/> judging the refusal. The set is here to keep
/// a batch climbing rather than to make it safe.</item>
/// <item>The walk is paid once per volume per operation, not once per path, which is bounded by the bin
/// where the per-path work is not.</item>
/// <item>One thread and no lock: the runner that owns a sequencer is single-threaded by design, and two
/// operations in one process get two sequencers over one directory, which the create above already
/// answers.</item>
/// </list>
/// Nothing here is measured: the ordering is a hypothesis about how a directory's index behaves.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class BinStemSequencer
{
    /// <summary>The character every entry name starts with, before the half's own letter.</summary>
    private const char EntryMarker = '$';

    /// <summary>The two characters before the part both halves of an entry share.</summary>
    private const int MarkerLength = 2;

    /// <summary>The half that describes the entry, and the half that is the entry.</summary>
    private const char IndexMarker = 'I';

    private const char PayloadMarker = 'R';

    /// <summary>
    /// The stems this bin directory already has, as the numbers <see cref="NativeBinLayout.FormatStem"/>
    /// spells them, and — as the set hands each one out — the ones this sequencer has given away since.
    /// </summary>
    /// <remarks>
    /// One set for both, because the property wanted is the same one: a stem is not handed out twice. The
    /// <see cref="HashSet{T}.Add"/> that tests it also records it, which is why the test costs one lookup
    /// and not two.
    /// </remarks>
    private readonly HashSet<long> _taken;

    /// <summary>
    /// What the next name will be, as an index into <see cref="NativeBinLayout.StemSpace"/>. Only ever
    /// climbs, which is what makes a name already in the bin cost one step rather than a search.
    /// </summary>
    private long _next;

    /// <summary>
    /// Whether the bin could not be walked, so this draws at random as it did before a sequencer existed.
    /// </summary>
    private readonly bool _random;

    /// <summary>
    /// Reads the bin and remembers every stem already in it.
    /// </summary>
    /// <param name="binDirectory">The volume's bin directory for this user, which the caller has created.</param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <remarks>
    /// A bin that cannot be walked is not an error: the fallback is what this drew names without, and an
    /// operation that would have succeeded on one of those must still succeed. Anything worth a failure of
    /// its own — a volume that is not mounted, a directory this user may not enter — is answered by
    /// <see cref="NativeBinLayout.EnsureBinDirectory"/> a line earlier, so what is caught here is a walk
    /// that failed on a directory that was just created.
    /// </remarks>
    internal BinStemSequencer(string binDirectory, CancellationToken cancellationToken)
    {
        _taken = [];
        _random = false;

        try
        {
            CollectStems(binDirectory, _taken, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _random = true;
        }
    }

    /// <summary>
    /// The next name to try.
    /// </summary>
    /// <param name="destination">Receives <see cref="NativeBinLayout.StemLength"/> characters.</param>
    /// <remarks>
    /// Climbs past whatever is taken and stops at <see cref="NativeBinLayout.StemSpace"/>, which is two
    /// billion stems and so not a thing a caller has. Random after that and after a walk that failed,
    /// because a name is a name however it was arrived at and the create that follows judges it either way.
    /// <para>
    /// Climbing past a name that is taken costs a hash lookup rather than a filesystem question, which is
    /// the whole difference between skipping six thousand names and asking the filesystem about six
    /// thousand.
    /// </para>
    /// </remarks>
    internal void Next(Span<char> destination)
    {
        if (!_random)
        {
            while (_next < NativeBinLayout.StemSpace)
            {
                if (_taken.Add(_next))
                {
                    NativeBinLayout.FormatStem(destination, _next);
                    _next++;
                    return;
                }

                _next++;
            }
        }

        NativeBinLayout.NextStem(destination);
    }

    /// <summary>
    /// Every stem the bin directory holds, in one walk.
    /// </summary>
    /// <param name="binDirectory">The bin to walk, once.</param>
    /// <param name="taken">Receives the stem of each entry, whichever half of it was found.</param>
    /// <param name="cancellationToken">Cancels the walk between entries.</param>
    /// <remarks>
    /// Both halves are read and neither is preferred, because either one alone would do and a payload
    /// without its record — a crash between the two writes, which this format allows and the next empty
    /// collects — still took the index. A name that is not one of this format's, or whose stem is not a
    /// number, is skipped rather than refused: this walks a directory holding whatever else was ever put in
    /// it, and nothing about that is this library's to rule on.
    /// </remarks>
    private static void CollectStems(
        string binDirectory,
        HashSet<long> taken,
        CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = true
        };

        var enumerable = new FileSystemEnumerable<bool>(
            binDirectory,
            (ref FileSystemEntry entry) =>
            {
                var name = entry.FileName;

                if (name.Length > MarkerLength + NativeBinLayout.StemLength
                    && name[0] == EntryMarker
                    && (name[1] == IndexMarker || name[1] == PayloadMarker)
                    && NativeBinLayout.TryParseStem(
                        name.Slice(MarkerLength, NativeBinLayout.StemLength),
                        out var stem))
                {
                    taken.Add(stem);
                }

                return true;
            },
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => true
        };

        foreach (var _ in enumerable)
        {
            // Asked per entry rather than per hundred, because the walk is the one part of a recycle that
            // grows with something other than the batch: a caller who cancels while a thirty-thousand-entry
            // bin is being read has already given the answer this would go on asking for.
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
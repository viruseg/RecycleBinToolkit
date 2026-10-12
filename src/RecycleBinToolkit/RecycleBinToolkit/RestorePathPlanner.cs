using System.Globalization;

namespace RecycleBinToolkit;

/// <summary>
/// What a restore does with an item whose destination path is already occupied.
/// </summary>
/// <remarks>
/// Four answers rather than one per policy, because the policies differ in what they promise a caller and
/// agree in what they can do to disk: the item goes to the path, stays in the Recycle Bin, takes the
/// occupant's place, or lands beside it under a name this library picked.
/// </remarks>
internal enum RestoreAction
{
    /// <summary>
    /// Put the item at the destination path, which is either free or — under a merge policy — a directory
    /// that is already there to merge into.
    /// </summary>
    Move,

    /// <summary>Leave the item in the Recycle Bin and touch nothing on disk.</summary>
    Skip,

    /// <summary>Overwrite whatever occupies the destination path.</summary>
    Replace,

    /// <summary>
    /// Put the item down beside the occupied path under the first free name — <c>report.txt</c> becoming
    /// <c>report (1).txt</c> — and leave what was there exactly as it is.
    /// </summary>
    Rename
}

/// <summary>
/// What to do with one item's destination path, where that item is to go, and what was in the way.
/// </summary>
/// <param name="Action">What to do about the item that wanted <paramref name="TargetPath"/>.</param>
/// <param name="TargetPath">
/// Where the item is to land: the path it asked for under every action but
/// <see cref="RestoreAction.Rename"/>, and the first free name beside it under that one.
/// </param>
/// <remarks>
/// The occupant's kind is carried rather than left for the caller to ask the filesystem again about, so
/// <see cref="RecycleRestoreConflict.OccupiedByDirectory"/> is read off the decision that was already made
/// rather than off a second lookup that could answer differently by then.
/// <para>
/// The default is a <see cref="RestoreAction.Move"/> to a <see langword="null"/> path, which no caller can
/// act on: every decision this library makes goes through <see cref="RestorePathPlanner.Decide"/>, which
/// always names a target.
/// </para>
/// </remarks>
internal readonly record struct RestoreDecision(RestoreAction Action, string TargetPath)
{
    /// <summary>Whether what occupies the path the item asked for is a directory rather than a file.</summary>
    /// <remarks>
    /// The path in question is the one the item wanted, not <see cref="TargetPath"/>: under
    /// <see cref="RestoreAction.Rename"/> the item lands at a free name of its own and nothing occupies that,
    /// while the path it collided with is still there and is what a conflict record names.
    /// <para>
    /// It is therefore <see langword="false"/> only where nothing was in the way at all — a
    /// <see cref="RestoreAction.Move"/> into a free path — and true under every action taken over an
    /// occupied one, <see cref="RestoreAction.Rename"/> among them. A file occupant gives
    /// <see langword="false"/> and a directory occupant <see langword="true"/> whichever of the four actions
    /// answered, because the answer is about what was found rather than about what was done about it.
    /// </para>
    /// </remarks>
    internal bool OccupiedByDirectory { get; init; }
}

/// <summary>
/// Answers the two questions a restore asks before it moves anything: what is in the way of a path, and what
/// should be done about the item that wanted it.
/// </summary>
/// <remarks>
/// Every answer is a decision rule and none of them can put an item anywhere else. No branch here picks a
/// path outside the one the caller named, and no branch renames, moves or removes something already on
/// disk — what is already there is left alone, overwritten, or set beside.
/// <para>
/// Nothing is written and nothing is moved here. The only thing that touches the filesystem is the question
/// "is this name free", asked of the filesystem because the filesystem is what answers it.
/// </para>
/// </remarks>
internal static class RestorePathPlanner
{
    /// <summary>
    /// The longest candidate assembled on the stack rather than on the heap. A path that reaches the
    /// classic <c>MAX_PATH</c> limit is 260 characters, and a candidate is that plus a bracketed number.
    /// </summary>
    private const int MaxStackCandidateLength = 320;

    /// <summary>
    /// Room for the widest number that can be written — an <see cref="int"/> is ten digits — plus the space
    /// and the two brackets around it.
    /// </summary>
    private const int NumberWidth = 13;

    /// <summary>
    /// The first name beside <paramref name="destinationPath"/> that nothing is using, counted from
    /// <c>1</c> — <c>report.txt</c> becoming <c>report (1).txt</c>, or <c>report (2).txt</c> when
    /// <c>report (1).txt</c> is taken.
    /// </summary>
    /// <param name="destinationPath">
    /// The path the item wanted, which by the time this is called is occupied. A trailing separator is
    /// trimmed, so a path written as a directory names the directory it points at rather than a name
    /// beginning with a space.
    /// </param>
    /// <returns>A path in the same directory as <paramref name="destinationPath"/>, under a name nothing is using.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The split is <see cref="Path.GetExtension(string)"/>'s — the last dot, not the first — so
    /// <c>archive.tar.gz</c> becomes <c>archive.tar (1).gz</c>. A name that is nothing but an extension takes
    /// the number after the whole name, because that call reports such a name as one long extension.</item>
    /// <item>Nothing is read back out of a name to decide where the next one starts: <c>report (5).txt</c>
    /// becomes <c>report (5) (1).txt</c>.</item>
    /// <item>A candidate is free when both <see cref="File.Exists"/> and <see cref="Directory.Exists"/> say
    /// no, rather than one of them chosen from what the name looks like. Each is false of the other's kind,
    /// so a directory named <c>Photos.2024</c> would otherwise be handed a number that is taken and the move
    /// would throw on a name this library picked itself.</item>
    /// <item>The candidate is composed in one span and has to become a <see cref="string"/> before the
    /// filesystem can be asked about it: one allocation per candidate examined.</item>
    /// </list>
    /// </remarks>
    internal static string WithFreeNumber(string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);

        // A path spelled with a trailing separator names a directory, and the name composed below goes beside it
        // rather than after the separator — which would make "X:\pics\" answer "X:\pics\ (1)", the name of a
        // directory called " (1)". TrimEndingDirectorySeparator takes one separator off and leaves a root
        // alone, so "X:\" and "\\server\share\" are still themselves; it is called until it stops shortening,
        // because a path spelled with two separators would otherwise keep one and answer just as wrongly.
        var trimmed = destinationPath;
        var shorter = Path.TrimEndingDirectorySeparator(trimmed);

        while (shorter.Length < trimmed.Length)
        {
            trimmed = shorter;
            shorter = Path.TrimEndingDirectorySeparator(trimmed);
        }

        var extension = Path.GetExtension(trimmed);

        // GetExtension answers with a suffix of the path, so the head needs no splitting: a candidate is
        // that head, the number, and the extension. A name that is all extension leaves a separator where
        // the head would end, and cutting there would put the number against a bare directory.
        var stemLength = trimmed.Length - extension.Length;
        var hasStem = stemLength > 0 && !IsSeparator(trimmed[stemLength - 1]);

        var headLength = hasStem ? stemLength : trimmed.Length;
        var tailLength = hasStem ? extension.Length : 0;

        Span<char> buffer = trimmed.Length + NumberWidth <= MaxStackCandidateLength
            ? stackalloc char[MaxStackCandidateLength]
            : new char[trimmed.Length + NumberWidth];

        for (var number = 1; ; number++)
        {
            var at = headLength;

            trimmed.AsSpan(0, headLength).CopyTo(buffer);

            buffer[at++] = ' ';
            buffer[at++] = '(';
            number.TryFormat(buffer[at..], out var written, default, CultureInfo.InvariantCulture);
            at += written;
            buffer[at++] = ')';

            extension.AsSpan(0, tailLength).CopyTo(buffer[at..]);

            var candidate = new string(buffer[..(at + tailLength)]);

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// What a merge does with one entry inside the tree whose destination is already occupied.
    /// </summary>
    /// <param name="destinationPath">The path the entry inside the restored tree wants.</param>
    /// <param name="mode">The directory policy the restore was asked for.</param>
    /// <returns>The action to take about that entry.</returns>
    /// <remarks>
    /// Only the three merge modes reach here at all — the walk begins under one of them, and
    /// <see cref="DecideMerged"/> is asked nothing else —
    /// <see cref="RestoreDirectoryConflict.MergeReplace"/> overwrites the file in the way,
    /// <see cref="RestoreDirectoryConflict.MergeRename"/> sets the incoming file down beside it, and
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/> leaves it alone, which is the default arm's own
    /// answer. The default arm then covers
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/> and anything from outside the set: the other two
    /// directory values are answered by <see cref="Decide"/> before a walk begins and never arrive, and an
    /// enum value from outside the set is best answered by the action that touches nothing.
    /// <para>
    /// <paramref name="destinationPath"/> is not read. A merge asks only about a path it has already found
    /// occupied, so the answer is the mode's alone; the path is what the caller walks and what
    /// <see cref="WithFreeNumber"/> renames into, not something the policy turns on.
    /// </para>
    /// <para>
    /// Asked of every entry whose destination is taken that is not a directory to merge into — a file under
    /// the squatter's own tree, a junction. Whether what occupies the path is a file or a directory is the
    /// walk's answer to carry, not this one's, and it is what the conflict record names.
    /// </para>
    /// </remarks>
    internal static RestoreAction DecideMerged(string destinationPath, RestoreDirectoryConflict mode)
    {
        _ = destinationPath;

        return mode switch
        {
            RestoreDirectoryConflict.MergeReplace => RestoreAction.Replace,
            RestoreDirectoryConflict.MergeRename => RestoreAction.Rename,
            _ => RestoreAction.Skip
        };
    }

    /// <summary>
    /// What to do about one item whose destination is <paramref name="destinationPath"/>, and what is in the
    /// way of it.
    /// </summary>
    /// <param name="destinationPath">The path the item wants.</param>
    /// <param name="isDirectory">Whether the item being restored is a directory.</param>
    /// <param name="options">The normalized options the restore is running under.</param>
    /// <returns>
    /// The action, the path the item is to land at, and whether what occupies
    /// <paramref name="destinationPath"/> is a directory. A free destination answers
    /// <see cref="RestoreAction.Move"/> at <paramref name="destinationPath"/> with no occupant; an occupied
    /// one reports the kind of what is there under every action, <see cref="RestoreAction.Rename"/> among
    /// them, because that is the path a conflict record names rather than the one the item ends up at.
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The policy is the one for the kind arriving, not the kind in the way. A destination holding the
    /// other kind is a conflict like any other, answered by the same branch a same-kind occupant is.</item>
    /// <item>A destination holding a directory answers <see cref="RestoreAction.Move"/> under every merge
    /// mode, and the tree is walked into what is there rather than moved whole. A directory is never
    /// replaced whole: the only way to replace one is to delete it first and find out afterwards whether the
    /// move was possible, and the bytes destroyed by that are the caller's.</item>
    /// <item>Both <c>Error</c> policies answer <see cref="RestoreAction.Skip"/>, as does an enum value from
    /// outside either policy. Whether the entry also lands in
    /// <see cref="RecycleBinRestoreResult.Failures"/> rather than only in
    /// <see cref="RecycleBinRestoreResult.Conflicts"/> is the caller's answer from the options, not a fifth
    /// action.</item>
    /// </list>
    /// </remarks>
    internal static RestoreDecision Decide(string destinationPath, bool isDirectory, RecycleBinOptions options)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(options);

        // The directory first, so a directory occupant — which a merge walk would ask about once for every file
        // inside it — is answered by one query. A free path costs both either way, and a path holds one kind
        // or the other, so the second is only reached when the first found no directory.
        var occupiedByDirectory = Directory.Exists(destinationPath);

        if (!occupiedByDirectory && !File.Exists(destinationPath))
        {
            return new RestoreDecision(RestoreAction.Move, destinationPath);
        }

        var action = isDirectory
            ? options.DirectoryConflict switch
            {
                RestoreDirectoryConflict.MergeSkip
                    or RestoreDirectoryConflict.MergeReplace
                    or RestoreDirectoryConflict.MergeRename => RestoreAction.Move,
                RestoreDirectoryConflict.RenameDirectory => RestoreAction.Rename,
                _ => RestoreAction.Skip
            }
            : options.FileConflict switch
            {
                RestoreFileConflict.Replace => RestoreAction.Replace,
                RestoreFileConflict.Rename => RestoreAction.Rename,
                _ => RestoreAction.Skip
            };

        // The free name is worked out only for the branch that renames. Every other action lands on the
        // path that was asked for, so the whole search is skipped where its answer would be discarded.
        var target = action == RestoreAction.Rename ? WithFreeNumber(destinationPath) : destinationPath;

        return new RestoreDecision(action, target) { OccupiedByDirectory = occupiedByDirectory };
    }

    /// <summary>
    /// Whether a character ends a directory name, which is what tells a name that is all extension from a
    /// name with a stem in front of its extension.
    /// </summary>
    private static bool IsSeparator(char character) =>
        character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
}
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
    /// <summary>Put the item at the destination path, which is either free or already there to merge into.</summary>
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
    /// <summary>Whether what occupies <see cref="TargetPath"/> is a directory rather than a file.</summary>
    /// <remarks>
    /// <see langword="false"/> for a free path, where there is nothing to be in the way, and under
    /// <see cref="RestoreAction.Rename"/>, where what was found is not where the item lands.
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
    /// <param name="destinationPath">The path the item wanted, which by the time this is called is occupied.</param>
    /// <returns>A path in the same directory as <paramref name="destinationPath"/>, under a name nothing is using.</returns>
    /// <remarks>
    /// The split is <see cref="Path.GetExtension(string)"/>'s: on the last dot rather than the first, so
    /// <c>archive.tar.gz</c> becomes <c>archive.tar (1).gz</c>. A name with no extension takes the number
    /// whole, and a name that is nothing but an extension — <c>.gitignore</c> — takes it after the whole
    /// name, because that call reports such a name as one long extension and there is no stem left in front
    /// of the number to put it in.
    /// <para>
    /// Nothing is read back out of a name to decide where the next one starts. <c>report (5).txt</c> is a
    /// name and not a statement about how many reports there are, so it becomes <c>report (5) (1).txt</c>;
    /// a caller that parsed the bracketed digits would ask for <c>report (5) (6).txt</c>, a name nothing
    /// has ever been asked for.
    /// </para>
    /// <para>
    /// Whether a candidate is taken is asked of the kind its name describes: a name carrying an extension is
    /// probed with <see cref="File.Exists"/> and a name without one with <see cref="Directory.Exists"/>.
    /// A directory whose own name carries a dot is therefore probed as a file, which is the one shape this
    /// naming gets wrong. Nothing here reads the directory it belongs to to find out, because the caller
    /// already knows which kind it is restoring and does not say so here.
    /// </para>
    /// <para>
    /// The candidate is composed in one span and the number written into it without an intermediate string.
    /// It still has to become a <see cref="string"/> before the filesystem can be asked about it — both
    /// probes take a <see cref="string"/> and nothing else, and that is the whole of the allocation here: one
    /// per candidate examined, and the one that is free is the one returned. A caller on the ordinary path
    /// never gets this far, because a destination nothing occupies is answered without naming anything.
    /// </para>
    /// </remarks>
    internal static string WithFreeNumber(string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);

        var extension = Path.GetExtension(destinationPath);

        // GetExtension answers with a suffix of the path, so the head needs no splitting: a candidate is
        // that head, the number, and the extension. A name that is all extension leaves a separator where
        // the head would end, and cutting there would put the number against a bare directory.
        var stemLength = destinationPath.Length - extension.Length;
        var hasStem = stemLength > 0 && !IsSeparator(destinationPath[stemLength - 1]);

        var headLength = hasStem ? stemLength : destinationPath.Length;
        var tailLength = hasStem ? extension.Length : 0;
        var isFile = extension.Length > 0;

        Span<char> buffer = destinationPath.Length + NumberWidth <= MaxStackCandidateLength
            ? stackalloc char[MaxStackCandidateLength]
            : new char[destinationPath.Length + NumberWidth];

        for (var number = 1; ; number++)
        {
            var at = headLength;

            destinationPath.AsSpan(0, headLength).CopyTo(buffer);

            buffer[at++] = ' ';
            buffer[at++] = '(';
            number.TryFormat(buffer[at..], out var written, default, CultureInfo.InvariantCulture);
            at += written;
            buffer[at++] = ')';

            extension.AsSpan(0, tailLength).CopyTo(buffer[at..]);

            var candidate = new string(buffer[..(at + tailLength)]);

            if (isFile ? !File.Exists(candidate) : !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// What a merge does with one file inside it whose destination is already occupied.
    /// </summary>
    /// <param name="destinationPath">The path the file inside the restored tree wants.</param>
    /// <param name="mode">The directory policy the restore was asked for.</param>
    /// <returns>The action to take about that file.</returns>
    /// <remarks>
    /// The three merge modes answer, and nothing else reaches here:
    /// <see cref="RestoreDirectoryConflict.MergeReplace"/> overwrites the file in the way,
    /// <see cref="RestoreDirectoryConflict.MergeRename"/> sets the incoming file down beside it, and
    /// <see cref="RestoreDirectoryConflict.MergeSkip"/> leaves it alone. The default arm is
    /// <see cref="RestoreAction.Skip"/> because the three values that would land on it never do — they are
    /// answered by <see cref="Decide"/> before the walk begins — and because an enum value from outside the
    /// set is best answered by the action that touches nothing.
    /// <para>
    /// <paramref name="destinationPath"/> is not read. A merge asks only about a path it has already found
    /// occupied, so the answer is the mode's alone; the path is what the caller walks and what
    /// <see cref="WithFreeNumber"/> renames into, not something the policy turns on.
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
    /// The action, the path the item is to land at, and whether what occupies it is a directory. A free
    /// destination answers <see cref="RestoreAction.Move"/> at <paramref name="destinationPath"/> with no
    /// occupant.
    /// </returns>
    /// <remarks>
    /// The policy is the one for the kind arriving, not the kind in the way. A destination holding the other
    /// kind is a conflict like any other — a file may be replaced by a directory or the reverse — and is
    /// answered by the same branch a same-kind occupant is.
    /// <para>
    /// A destination holding a directory answers <see cref="RestoreAction.Move"/> under every merge mode.
    /// That is not the same as moving a free directory whole: the tree walk merges into what is there and
    /// asks <see cref="DecideMerged"/> about each file inside it. A directory is never replaced whole,
    /// because the only way to replace one is to delete it first and find out afterwards whether the move
    /// was possible at all, and the bytes destroyed by that are the caller's.
    /// </para>
    /// <para>
    /// Both <c>Error</c> policies answer <see cref="RestoreAction.Skip"/>, as does an enum value from
    /// outside either policy. Which of the two a refusal is — whether the item also lands in
    /// <see cref="RecycleBinRestoreResult.Failures"/> rather than only in
    /// <see cref="RecycleBinRestoreResult.Conflicts"/> — is the caller's answer from the options, not a
    /// fifth action.
    /// </para>
    /// </remarks>
    internal static RestoreDecision Decide(string destinationPath, bool isDirectory, RecycleBinOptions options)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(options);

        // The directory first, so a directory occupant — which is the case the merge walk asks about for every
        // file inside it — is answered by one query. A free path costs both either way, and a path holds
        // one kind or the other, so the second is only reached when the first found no directory.
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
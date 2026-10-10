namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Reading a volume root off a command line, and refusing anything that is not one.
/// </summary>
/// <remarks>
/// The argument is read the same way whatever it is spelled like, and what the volume behind it is
/// asked to do is not decided here: any volume root the caller names is usable, and whether a given
/// case can say anything about the volume it was given is that case's question, answered by reading
/// the library's own answer rather than by this method.
/// </remarks>
internal static class VolumeRoot
{
    /// <summary>
    /// The root as a volume root is spelled, with a trailing separator.
    /// </summary>
    /// <param name="value">What the caller typed.</param>
    /// <returns>
    /// The root with its separators reduced to backslashes and its own spelling otherwise left alone. A
    /// forward-slash spelling of a share is converted because the command line is the only thing standing
    /// between the two; the case of a drive letter is not, because paths are recorded
    /// in their own case whatever this returned, so rewriting it here would make one side of a comparison
    /// agree with the other without making either of them right.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> does not name a volume root. A path inside a volume, a relative path and a
    /// bare drive letter all land here: each of them resolves against something other than the volume it
    /// names, so a directory created under one would land somewhere the caller did not ask about.
    /// </exception>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A volume root is required and nothing was given.");
        }

        // A bare "W:" is drive-relative, and Path.GetPathRoot answers "W:" for it — a root by the same rule
        // that says a directory is one, and resolved against whatever directory the process happens to have
        // current for that drive. Requiring a trailing separator is what tells the two apart.
        var spelled = value.StartsWith("//", StringComparison.Ordinal)
            ? value.Replace('/', '\\')
            : value;

        if (!spelled.EndsWith(Path.DirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"\"{value}\" is not a volume root: it names no trailing separator, so what it names would be resolved " +
                "against the current directory rather than against a volume. Name the root itself, such as W:\\ or \\\\server\\share\\.");
        }

        if (!Path.IsPathFullyQualified(spelled))
        {
            throw new ArgumentException($"\"{value}\" is not a fully qualified path, so it names no volume root.");
        }

        // Compared with the trailing separator left off on both sides, because GetPathRoot is not symmetric about
        // it: `Path.GetPathRoot(@"W:\")` answers `W:\` while `Path.GetPathRoot(@"\\server\share\")` answers
        // `\\server\share\`. Both name the volume the caller typed.
        var withoutSeparator = spelled[..^1];
        var root = Path.GetPathRoot(spelled);

        if (!string.Equals(root?.TrimEnd(Path.DirectorySeparatorChar), withoutSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"\"{value}\" is a path inside a volume rather than a volume root: Path.GetPathRoot answers " +
                $"\"{root}\" for it. Name the root itself, so the suite creates its scratch " +
                "directory beside your files rather than inside them.");
        }

        return spelled;
    }

    /// <summary>
    /// The root the caller named, checked against what this machine actually has mounted.
    /// </summary>
    /// <param name="value">What the caller typed.</param>
    /// <param name="flag">
    /// The flag the value came from, named in the failure so a reader is told which argument to correct.
    /// </param>
    /// <returns>The root, with the caller's spelling and backslash separators.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not name a volume root.</exception>
    /// <exception cref="InvalidOperationException">
    /// The machine has no such volume. <c>Normalize</c> cannot tell a mounted root from an unmounted one —
    /// both are spelled like roots — so the check is made here, before anything is created on it.
    /// </exception>
    public static string Require(string? value, string flag)
    {
        var root = Normalize(value);

        if (!IsMounted(root))
        {
            throw new InvalidOperationException(
                $"{flag} was given \"{value}\", which is a root this machine does not have. Nothing is mounted there, " +
                "so nothing can be written to it. Check the drive letter or the share name, or mount it and run again.");
        }

        return root;
    }

    /// <summary>
    /// Whether a volume root names a volume this machine has.
    /// </summary>
    /// <remarks>
    /// A share is asked through the filesystem rather than through <see cref="DriveInfo"/>, which reports
    /// only drive letters; a network path that is unreachable answers <see cref="Directory.Exists"/> false,
    /// which is the answer this wants. A root read from a mounted drive is asked through the same call so
    /// that an empty card reader and a disconnected drive both come back as absent.
    /// </remarks>
    private static bool IsMounted(string root) => Directory.Exists(root);

    /// <summary>
    /// The root of the volume a path sits on, or the path itself when it already is one.
    /// </summary>
    /// <remarks>
    /// Handed a directory, this answers the root of the volume rather than refusing: the probe needs to ask
    /// a question about a volume, and a question about a volume is only asked of a volume.
    /// </remarks>
    public static string Of(string path) => Path.GetPathRoot(path) ?? throw new ArgumentException($"\"{path}\" names no volume root.", nameof(path));
}
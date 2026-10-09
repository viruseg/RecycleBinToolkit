namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// A directory this run created under a directory it already owns, and the only handle needed to remove
/// it.
/// </summary>
/// <remarks>
/// The suite writes files and deletes directories, so the question of which directories it is allowed to
/// delete is settled here rather than at each call site. A claim is created with a name that did not exist,
/// is always a direct child of a parent inside the run's own directory, and is removed by the exact path it
/// returned — none of which a name written into the source could give, because
/// <c>Directory.CreateDirectory</c> reports success for a directory somebody else already had.
/// <para>
/// The two entry points are what keep the suite inside that directory. <see cref="ClaimRunRoot"/> takes a
/// volume root and is allowed once in a process; <see cref="Claim(string)"/> takes only a parent already
/// inside the run's directory and is refused otherwise. One method with two rules would not hold: handed
/// the volume root, a case would create a directory beside everything the run works in, which is the one
/// thing the working directory exists to prevent.
/// </para>
/// </remarks>
internal sealed class ScratchDirectory
{
    /// <summary>How many names a claim may try before it gives up rather than spin.</summary>
    private const int MaxNameAttempts = 64;

    /// <summary>How many times a removal is retried while something still holds a file.</summary>
    private const int MaxDeleteAttempts = 12;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    private static ScratchDirectory? _runRoot;

    private ScratchDirectory(string path, string parent)
    {
        FullPath = path;
        Parent = parent;
    }

    /// <summary>
    /// Absolute path of the claimed directory, with a trailing separator.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// The directory this one was claimed under, with a trailing separator. Every removal is of a path
    /// inside it, so it is what a message about a failed removal has to name.
    /// </summary>
    /// <remarks>
    /// A claim takes a parent this run already owns. For the run's own directory that parent is the
    /// volume root the caller named, because <see cref="ClaimRunRoot"/> is the one claim taken outside the
    /// run; for every other claim it is an ordinary directory inside that run's directory.
    /// </remarks>
    public string Parent { get; }

    /// <summary>
    /// The full path of the run's own directory, or <see langword="null"/> before
    /// <see cref="ClaimRunRoot"/> has claimed it. This is the boundary every other claim is measured
    /// against.
    /// </summary>
    internal static string? RunRootPath => _runRoot?.FullPath;

    /// <summary>
    /// Claims the run's own directory directly under <paramref name="volumeRoot"/>, once per process.
    /// </summary>
    /// <param name="volumeRoot">
    /// The root of a volume the caller named, such as <c>W:\</c> or <c>\\server\share\</c>. A path that is
    /// not a root is refused, because a directory created inside one would be created inside somebody's
    /// data and removed with its parent.
    /// </param>
    /// <returns>The run's own directory.</returns>
    /// <remarks>
    /// This is the one claim that reaches out of the run, and it reaches out exactly once. Every later claim
    /// is measured against what it returns, so a second one would sit beside the directory every case
    /// writes under — which is what the process-wide guard exists to refuse.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="volumeRoot"/> is not a volume root.</exception>
    /// <exception cref="InvalidOperationException">
    /// The run's own directory has already been claimed, which would make this process two runs at once.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Every name tried under <paramref name="volumeRoot"/> was already taken. Left spinning, the run would
    /// report nothing until it was killed.
    /// </exception>
    public static ScratchDirectory ClaimRunRoot(string volumeRoot)
    {
        var root = VolumeRoot.Normalize(volumeRoot);

        if (_runRoot is not null)
        {
            throw new InvalidOperationException(
                $"This process already works on {_runRoot.Parent}, in {_runRoot.FullPath}, and {root} was named as a second run root. " +
                "One process is one run: a second claim would create a directory beside the one every case writes under, " +
                "and nothing would be there to remove it.");
        }

        _runRoot = ClaimUnder(root, () => Guid.NewGuid().ToString("N"));

        return _runRoot;
    }

    /// <summary>
    /// Claims a directory of the suite's own directly under <paramref name="parent"/>.
    /// </summary>
    /// <param name="parent">
    /// A directory inside the run's own directory, spelled as a fully qualified path. The volume root
    /// itself is refused, because a claim taken there would be created beside everything this run works in
    /// and removed without it.
    /// </param>
    /// <returns>The claimed directory.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="parent"/> is null, empty, not fully qualified, or unreadable as a path; or it is not
    /// inside the run's own directory once <c>..</c> segments are resolved. Nothing is created.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The run's own directory has not been claimed, so there is nothing for the claim to sit inside.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Every name tried was already taken. Left spinning, the run would report nothing until it was killed.
    /// </exception>
    public static ScratchDirectory Claim(string parent) => Claim(parent, () => Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Claims a directory under <paramref name="parent"/>, taking each candidate name from
    /// <paramref name="nameFor"/>.
    /// </summary>
    /// <param name="parent">
    /// A fully qualified directory inside the run's own directory. See <see cref="Claim(string)"/>.
    /// </param>
    /// <param name="nameFor">
    /// Produces the next name to try. Called again only for a name that is already taken, so the number of
    /// calls is the number of collisions plus one.
    /// </param>
    /// <returns>The claimed directory.</returns>
    /// <remarks>
    /// The seam exists so the collision handling can be pinned: a generated name never collides in practice,
    /// which is the same reason it is untestable without being handed the names directly.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="parent"/> is null, empty, not fully qualified, or unreadable as a path; or it is not
    /// inside the run's own directory once <c>..</c> segments are resolved. Nothing is created.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The run's own directory has not been claimed, so there is nothing for the claim to sit inside.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Every name tried was already taken. Left spinning, the run would report nothing until it was killed.
    /// </exception>
    internal static ScratchDirectory Claim(string parent, Func<string> nameFor)
    {
        ArgumentNullException.ThrowIfNull(nameFor);

        return ClaimUnder(ParentInsideRunRoot(parent), nameFor);
    }

    /// <summary>
    /// Creates and returns <paramref name="name"/> under this claim, so several consumers share one
    /// directory without sharing a name.
    /// </summary>
    /// <param name="name">
    /// What the directory is for, such as <c>scratch</c> or <c>long</c>. A name of the caller's choosing
    /// rather than a generated one: it is a child of a directory that did not exist a moment ago, so there
    /// is nothing for it to collide with, and a fixed name is what makes it readable in a failure.
    /// </param>
    /// <returns>The full path of the created directory, with a trailing separator.</returns>
    public string Subdirectory(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"{Quote(name)} is not a directory name Windows accepts.", nameof(name));
        }

        var path = System.IO.Path.Combine(FullPath, name) + System.IO.Path.DirectorySeparatorChar;

        if (!Directory.CreateDirectory(path).Exists)
        {
            throw new IOException($"Could not create {path}");
        }

        return path;
    }

    /// <summary>
    /// Removes the claimed directory and everything in it.
    /// </summary>
    /// <remarks>
    /// A recursive delete, which is safe here for one reason: the path is the one this run created, and the
    /// method is <c>internal</c> to the test assembly so nothing else can pass it anything else. Safe to
    /// call more than once, and on a claim whose directory is already gone.
    /// </remarks>
    public void Remove()
    {
        if (!Directory.Exists(FullPath))
        {
            return;
        }

        var target = LongPathStager.PathForIo(FullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MaxDeleteAttempts)
                {
                    throw new IOException(
                        $"Could not remove {FullPath} after {MaxDeleteAttempts} attempts. Something still holds a file in it; " +
                        $"every path under it belongs to this run, so what is left behind is scratch data rather than anything of yours. {ex.Message}",
                        ex);
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    /// <summary>
    /// Takes the next free name under an already-vetted parent and creates the directory, which is the whole
    /// of what a claim does once its parent is settled.
    /// </summary>
    private static ScratchDirectory ClaimUnder(string parent, Func<string> nameFor)
    {
        for (var attempt = 1; ; attempt++)
        {
            var name = nameFor();

            if (name is null || name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(ScratchDirectory)} was offered {Quote(name)} as a directory name under {parent}, which is not a name Windows accepts.");
            }

            var path = System.IO.Path.Combine(parent, name) + System.IO.Path.DirectorySeparatorChar;

            if (Directory.Exists(path))
            {
                if (attempt >= MaxNameAttempts)
                {
                    throw new InvalidOperationException(
                        $"{nameof(ScratchDirectory)} tried {MaxNameAttempts} names under {parent} and every one of them was taken, the last being {Quote(name)}. " +
                        "Nothing was created. A directory where no fresh name is available cannot host another claim.");
                }

                continue;
            }

            Directory.CreateDirectory(path);

            return new ScratchDirectory(path, parent);
        }
    }

    /// <summary>
    /// The parent a claim may sit under in its full form, which is the one it was handed spelled out or a
    /// refusal naming it.
    /// </summary>
    /// <remarks>
    /// The comparison is over resolved paths, not over the text a caller passed. <c>..</c> would otherwise
    /// pass a prefix test and still leave the run's directory — <c>E:\{guid}\..\elsewhere</c> begins with
    /// <c>E:\{guid}\</c> and resolves to <c>E:\elsewhere</c> — and a correct parent written with forward
    /// slashes would be refused, because <c>E:/{guid}/</c> and <c>E:\{guid}\</c> are the same directory
    /// spelled two ways. The normalized form is what is returned, so the directory created is the one that
    /// was checked rather than a re-parse of the text.
    /// <para>
    /// A claim with no run's own directory to sit inside is a state this suite cannot be put into: the
    /// runner claims the run root before it selects a single case, so by the time any code here runs the
    /// boundary exists. The guard is here rather than assumed because a refactor that moved this check
    /// behind <see cref="ClaimRunRoot"/>, or let a case name the run root itself, would reopen the route
    /// out of the working directory silently — and a null reference would not say what had gone wrong.
    /// </para>
    /// <para>
    /// Nothing asserts that branch, and that is deliberate. Reaching it from a case needs either a seam
    /// that hands over the run root — which is precisely what the two refusals in
    /// <c>ScratchSuite.ClaimRefusesAParentOutsideTheRunRoot</c> and
    /// <c>ScratchSuite.ClaimRunRootRefusesASecondRunInOneProcess</c> exist to forbid — or a private static
    /// cleared by reflection, which would leave the process in a state no later case could rely on. A case
    /// that cannot run would print <c>SKIP</c> on every run and teach the reader to ignore the one line
    /// that carries that meaning, so the guard is left to state its own reachability here instead.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="parent"/> is null, empty, not fully qualified, or unreadable as a path; or it is not
    /// inside the run's own directory once resolved. Every one of these is checked before anything is
    /// created, so a refusal leaves the volume exactly as it was.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No run's own directory has been claimed, so there is no boundary to measure the parent against and
    /// nothing this could fall back to but a path the caller never named.
    /// </exception>
    private static string ParentInsideRunRoot(string parent)
    {
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException("A parent is required, and nothing was given.", nameof(parent));
        }

        // Refused before it is resolved, because resolving it would answer with the process's current
        // directory. That is the same ambient state VolumeRoot.Normalize refuses for a bare drive letter,
        // and a parent that resolves against it is not one this run owns.
        if (!Path.IsPathFullyQualified(parent))
        {
            throw new ArgumentException(
                $"{Quote(parent)} is not a fully qualified path, so nothing was created under it. " +
                "A relative parent names no directory until it is resolved, and resolution would use whatever " +
                "current directory this process happens to have rather than the directory the caller meant.",
                nameof(parent));
        }

        var runRoot = _runRoot ?? throw new InvalidOperationException(
            $"{nameof(ScratchDirectory)} has no run's own directory to sit inside: {nameof(ClaimRunRoot)} has not claimed one, " +
            "so there is nothing to create a directory under that this run also owns.");

        string full;

        try
        {
            full = Path.GetFullPath(parent);
        }
        catch (Exception ex)
        {
            // Deliberately broad, and deliberately a refusal. Nothing has been created at this point, so the
            // worst a catch this wide can do is turn a crash into a rejection; the alternative is letting an
            // exception nobody anticipated escape from a check whose whole job is to fail closed. A path
            // this cannot read is not a path this run may write under.
            throw new ArgumentException(
                $"{Quote(parent)} could not be read as a path, so nothing was created under it and no claim was taken. {ex.Message}",
                nameof(parent),
                ex);
        }

        if (!full.StartsWith(BoundedBySeparator(runRoot.FullPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{Quote(full)} is not inside the run's own directory {runRoot.FullPath}, so nothing was created there. " +
                "A claim is taken under a directory this run already owns; the volume root is the one place a claim may be created, " +
                $"and {nameof(ClaimRunRoot)} has already made it.",
                nameof(parent));
        }

        return full;
    }

    /// <summary>
    /// The run's own directory spelled so a prefix comparison against it stops at a separator: without one,
    /// the run root's name is a prefix of any sibling whose name begins with it, and a sibling would read
    /// as a child.
    /// </summary>
    /// <remarks>
    /// Only one separator is added. <see cref="FullPath"/> already carries a trailing one, and a second
    /// would leave a path the comparison can never match.
    /// </remarks>
    private static string BoundedBySeparator(string runRoot) =>
        runRoot.EndsWith(System.IO.Path.DirectorySeparatorChar) || runRoot.EndsWith(System.IO.Path.AltDirectorySeparatorChar)
            ? runRoot
            : runRoot + System.IO.Path.DirectorySeparatorChar;

    private static string Quote(string? value) => value is null ? "<null>" : $"\"{value}\"";
}
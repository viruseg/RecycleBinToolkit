namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// A directory this run created under the volume root the caller named, and the only handle needed to
/// remove it.
/// </summary>
/// <remarks>
/// The suite writes files and deletes directories, so the question of which directories it is allowed to
/// delete is settled here rather than at each call site. A claim is created with a name that did not exist,
/// is always a direct child of a volume root, and is removed by the exact path it returned — none of which
/// a name written into the source could give, because <c>Directory.CreateDirectory</c> reports success for a
/// directory somebody else already had.
/// </remarks>
internal sealed class ScratchDirectory
{
    /// <summary>How many names a claim may try before it gives up rather than spin.</summary>
    private const int MaxNameAttempts = 64;

    /// <summary>How many times a removal is retried while something still holds a file.</summary>
    private const int MaxDeleteAttempts = 12;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    private ScratchDirectory(string path, string volume)
    {
        FullPath = path;
        Volume = volume;
    }

    /// <summary>
    /// Absolute path of the claimed directory, with a trailing separator.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    /// The volume root this directory was claimed under, with a trailing separator. Every removal is of a
    /// path inside it, so it is what a message about a failed removal has to name.
    /// </summary>
    public string Volume { get; }

    /// <summary>
    /// Claims a directory of the suite's own directly under <paramref name="volumeRoot"/>.
    /// </summary>
    /// <param name="volumeRoot">
    /// The root of a volume the caller named, such as <c>W:\</c> or <c>\\server\share\</c>. A path that is
    /// not a root is refused, because a directory created inside one would be created inside somebody's
    /// data and removed with its parent.
    /// </param>
    /// <returns>The claimed directory.</returns>
    /// <exception cref="ArgumentException"><paramref name="volumeRoot"/> is not a volume root.</exception>
    /// <exception cref="InvalidOperationException">
    /// Every name tried was already taken. Left spinning, the run would report nothing until it was killed.
    /// </exception>
    public static ScratchDirectory Claim(string volumeRoot) => Claim(volumeRoot, () => Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Claims a directory under a root, taking each candidate name from <paramref name="nameFor"/>.
    /// </summary>
    /// <param name="volumeRoot">The root of a volume the caller named. See <see cref="Claim(string)"/>.</param>
    /// <param name="nameFor">
    /// Produces the next name to try. Called again only for a name that is already taken, so the number of
    /// calls is the number of collisions plus one.
    /// </param>
    /// <returns>The claimed directory.</returns>
    /// <remarks>
    /// The seam exists so the collision handling can be pinned: a generated name never collides in practice,
    /// which is the same reason it is untestable without being handed the names directly.
    /// </remarks>
    internal static ScratchDirectory Claim(string volumeRoot, Func<string> nameFor)
    {
        ArgumentNullException.ThrowIfNull(nameFor);

        var root = VolumeRoot.Normalize(volumeRoot);

        for (var attempt = 1; ; attempt++)
        {
            var name = nameFor();

            if (name is null || name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(ScratchDirectory)} was offered {Quote(name)} as a directory name under {root}, which is not a name Windows accepts.");
            }

            var path = System.IO.Path.Combine(root, name) + System.IO.Path.DirectorySeparatorChar;

            if (Directory.Exists(path))
            {
                if (attempt >= MaxNameAttempts)
                {
                    throw new InvalidOperationException(
                        $"{nameof(ScratchDirectory)} tried {MaxNameAttempts} names under {root} and every one of them was taken, the last being {Quote(name)}. " +
                        "Nothing was created. A volume where no fresh name is available cannot host this suite's scratch directory.");
                }

                continue;
            }

            Directory.CreateDirectory(path);

            return new ScratchDirectory(path, root);
        }
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

    private static string Quote(string? value) => value is null ? "<null>" : $"\"{value}\"";
}
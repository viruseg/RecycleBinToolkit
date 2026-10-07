namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// The single scratch directory every test writes into. It lives inside the repository, so no test
/// ever touches a path outside the project.
/// </summary>
internal sealed class ScratchWorkspace : IDisposable
{
    private const string ExtendedPrefix = @"\\?\";

    private const int MaxDeleteAttempts = 12;

    /// <summary>
    /// Number of files <see cref="CreateFixtureSet"/> produces. Large enough that a run recycles a
    /// batch worth measuring, small enough that regenerating it stays cheap.
    /// </summary>
    public const int FixtureFileCount = 94;

    /// <summary>
    /// Size of every generated file. Uniform on purpose: the tests assert on file counts and on the
    /// Recycle Bin totals, never on per-file sizes.
    /// </summary>
    public const int FixtureFileSizeBytes = 64 * 1024;

    private const int DeterministicSeed = 20261007;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    private static readonly byte[] FixturePattern = BuildFixturePattern();

    private static int _sequence;

    /// <summary>
    /// Absolute path of the scratch root, always with a trailing separator.
    /// </summary>
    public static string Root { get; } =
        Path.Combine(ResolveRepositoryRoot(), "Test") + Path.DirectorySeparatorChar;

    private bool _disposed;

    /// <summary>
    /// Deletes any leftover contents and recreates an empty scratch root.
    /// </summary>
    public void Recreate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DeleteTree();

        if (!Directory.CreateDirectory(Root).Exists)
        {
            throw new IOException($"Could not create scratch root {Root}");
        }
    }

    /// <summary>
    /// Creates the standard set of generated files in the scratch root, named <c>(1).jpg</c> through
    /// <c>(94).jpg</c>. The parenthesised names and the image extension are kept from the sample
    /// files this suite used to commit, because a Shell rename has to cope with exactly those names.
    /// </summary>
    /// <returns>Full paths of the created files, in name order.</returns>
    public IReadOnlyList<string> CreateFixtureSet()
    {
        var created = new List<string>(FixtureFileCount);

        for (var index = 1; index <= FixtureFileCount; index++)
        {
            created.Add(CreateFile($"({index}).jpg"));
        }

        return created;
    }

    /// <summary>
    /// Creates one file of the requested size filled with random bytes.
    /// </summary>
    /// <returns>Full path of the created file.</returns>
    public string CreateRandomFile(int sizeBytes)
    {
        var path = NextPath();

        var buffer = new byte[Math.Min(sizeBytes, 1 << 20)];
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var remaining = sizeBytes;
            while (remaining > 0)
            {
                Random.Shared.NextBytes(buffer.AsSpan(0, Math.Min(remaining, buffer.Length)));
                var chunk = Math.Min(remaining, buffer.Length);
                stream.Write(buffer, 0, chunk);
                remaining -= chunk;
            }
        }

        return path;
    }

    /// <summary>
    /// Creates <paramref name="count"/> random files of the requested size each.
    /// </summary>
    /// <returns>Full paths of the created files.</returns>
    public IReadOnlyList<string> CreateRandomFiles(int count, int sizeBytes)
    {
        var paths = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            paths.Add(CreateRandomFile(sizeBytes));
        }

        return paths;
    }

    /// <summary>
    /// Removes the scratch root. Safe to call more than once and when the root is already gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteTree();
    }

    private static void DeleteTree()
    {
        if (!Directory.Exists(ScratchWorkspace.Root))
        {
            return;
        }

        // The extended-length prefix is what makes the standard file APIs work on a path the tests
        // deliberately build past MAX_PATH. Without it, cleanup throws and the deep tree is left
        // behind, where a workspace file listing trips over paths it cannot enumerate.
        var root = ExtendedPrefix + ScratchWorkspace.Root.TrimEnd(Path.DirectorySeparatorChar);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearReadOnly(root);
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The tests create and recycle thousands of files in this one directory, so the
                // Windows indexer and antivirus routinely have a handle open on a file for a few
                // milliseconds after it appears. That is a transient hold, not a real failure, and
                // retrying settles it. Letting it through instead would fail a test that had already
                // passed, on a file the library never touched.
                if (attempt >= MaxDeleteAttempts)
                {
                    throw new IOException(
                        $"Could not remove {ScratchWorkspace.Root} after {MaxDeleteAttempts} attempts. " +
                        $"Something still holds a file there; this suite's own handles are all disposed by now. {ex.Message}",
                        ex);
                }

                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    private static void ClearReadOnly(string root)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                TryClearAttributes(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be enumerated or cleared surfaces on Directory.Delete instead.
        }
    }

    private static void TryClearAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be cleared will surface on Directory.Delete instead.
        }
    }

    private static string NextPath() =>
        Path.Combine(Root, $"random-{Environment.ProcessId}-{Interlocked.Increment(ref _sequence)}.bin");

    private string CreateFile(string fileName)
    {
        var path = Path.Combine(Root, fileName);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(FixturePattern);

        return path;
    }

    /// <summary>
    /// One shared buffer of random-looking bytes. It is generated once from a fixed seed so a failed
    /// run can be reproduced exactly, and it stays readable as ordinary binary content, which is what
    /// the read-a-slice tests actually need.
    /// </summary>
    private static byte[] BuildFixturePattern()
    {
        var buffer = new byte[FixtureFileSizeBytes];
        new Random(DeterministicSeed).NextBytes(buffer);
        return buffer;
    }

    /// <summary>
    /// Walks up from the test binary to the directory holding both <c>src</c> and <c>tests</c>.
    /// A structural marker rather than a file name: the solution file sits under
    /// <c>src/RecycleBinToolkit/</c>, so walking up for it would stop one level too low.
    /// </summary>
    /// <exception cref="InvalidOperationException">No parent directory holds both source trees.</exception>
    private static string ResolveRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find the repository root — a directory holding both 'src' and 'tests' — in any parent of {AppContext.BaseDirectory}.");
    }
}
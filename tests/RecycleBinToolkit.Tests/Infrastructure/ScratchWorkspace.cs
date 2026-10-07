namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// The single scratch directory every test writes into. It lives next to the fixture directory
/// inside the repository, so no test ever touches a path outside the project.
/// </summary>
internal sealed class ScratchWorkspace : IDisposable
{
    private const string ExtendedPrefix = @"\\?\";

    private const int MaxDeleteAttempts = 12;

    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(250);

    private static int _sequence;

    /// <summary>
    /// Absolute path of the scratch root, always with a trailing separator.
    /// </summary>
    public static string Root { get; } =
        Path.Combine(Path.GetDirectoryName(FixtureFiles.SourceDirectory)!, "Test") + Path.DirectorySeparatorChar;

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
    /// Copies every fixture file into the scratch root, preserving names.
    /// </summary>
    /// <returns>Full paths of the copies, in fixture order.</returns>
    public IReadOnlyList<string> CopyAllFixtures()
    {
        var copied = new List<string>();

        foreach (var source in FixtureFiles.All())
        {
            var target = Path.Combine(Root, Path.GetFileName(source));
            File.Copy(source, target, overwrite: true);
            copied.Add(target);
        }

        return copied;
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
}
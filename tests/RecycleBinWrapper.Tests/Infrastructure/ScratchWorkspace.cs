namespace RecycleBinWrapper.Tests.Infrastructure;

/// <summary>
/// The single scratch directory every test writes into. It lives next to the fixture directory
/// inside the repository, so no test ever touches a path outside the project.
/// </summary>
internal sealed class ScratchWorkspace : IDisposable
{
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
        var root = Root.TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            TryClearAttributes(path);
        }

        Directory.Delete(root, recursive: true);
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
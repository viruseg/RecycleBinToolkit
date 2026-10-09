namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// The single scratch directory every test writes into.
/// </summary>
/// <remarks>
/// Its location is not decided here: <c>--scratch</c> names the volume, and <see cref="Configure"/> claims a
/// directory of this run's own inside that volume. Nothing in the source names a path, so the suite cannot be
/// pointed twice at the same directory and cannot delete a directory it did not create.
/// <para>
/// Almost every case recycles a path on <see cref="Root"/>'s volume, and that only works because the volume
/// reads as recycling. Windows records that per volume, under
/// <c>BitBucket\Volume\{GUID}\NukeOnDelete=0</c>; a volume with no such key is answered by asking the
/// Shell instead, and the answer it gives — <see cref="RecycleBinAvailabilityReason.Indeterminate"/> or
/// <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/> — is one the gate refuses.
/// <see cref="Recreate"/> asserts the requirement rather than leaving the next machine's run to fail a
/// hundred cases at once.
/// </remarks>
internal sealed class ScratchWorkspace : IDisposable
{
    private static ScratchDirectory? _runRoot;

    private const int MaxDeleteAttempts = 12;

    /// <summary>
    /// Size of the standard fixture set. Large enough that a load case recycles a batch worth measuring,
    /// which is why a case that only needs a handful of files asks for fewer rather than creating these
    /// and discarding the rest.
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
    /// The parent of this run's own claim, with a trailing separator — which is the volume root
    /// <c>--scratch</c> named, because that claim is the one taken outside the run's directory. Every case
    /// that asks a question of the Shell asks it about this string rather than about a drive letter
    /// written into the source, which is what lets the whole suite run on a volume the caller named.
    /// </summary>
    public static string VolumeRoot => _runRoot?.Parent ?? throw new InvalidOperationException(
        $"{nameof(Configure)} has not run, so no volume has been named. Pass --scratch <root> on the command line.");

    /// <summary>
    /// The directory this run claimed, with a trailing separator. Printed by the runner so a reader can see
    /// where a run worked, and named in every message about a failed removal.
    /// </summary>
    public static string RunRoot => _runRoot?.FullPath ?? throw new InvalidOperationException(
        $"{nameof(Configure)} has not run, so no scratch directory has been claimed.");

    /// <summary>
    /// Absolute path of the scratch root, always with a trailing separator.
    /// </summary>
    /// <remarks>
    /// A path, not a creator: reading it never brings the directory into existence, because the cases that
    /// assert the directory is <em>gone</em> read it to find out. Use <see cref="Recreate"/> to create it.
    /// </remarks>
    public static string Root => PathUnder("scratch");

    /// <summary>
    /// Absolute path of the root for cases that build <em>directories</em> past the Shell's length
    /// limit, always with a trailing separator.
    /// </summary>
    /// <remarks>
    /// A separate child of the run's directory rather than a subdirectory of <see cref="Root"/>, because the
    /// cases that build these clean up after themselves with a recursive delete of everything they made, and
    /// <see cref="Root"/> holds the files other cases are still working on. A long directory has to be built
    /// from nested components — one component cannot exceed 255 characters — so it is a chain of deep
    /// directories rather than one awkward name, which is exactly the shape a recursive delete of a mixed
    /// tree should not be asked to leave behind.
    /// <para>
    /// A long <em>file</em> path does not have this problem: it is one flat component under
    /// <see cref="Root"/>, so <see cref="Root"/> remains where those belong.
    /// </para>
    /// </remarks>
    public static string LongDirectoryRoot => PathUnder("long");

    /// <summary>
    /// Claims this run's directory tree under <paramref name="volumeRoot"/>, once per process.
    /// </summary>
    /// <param name="volumeRoot">The root of a volume the caller named, as <c>--scratch</c> was read.</param>
    /// <remarks>
    /// Called by the runner before a single case runs, so every case and every message about a path agrees
    /// on where the run works. Calling it twice with different roots would be two runs sharing one process,
    /// and the second would quietly write beside the first's tree — which is what
    /// <see cref="ScratchDirectory.ClaimRunRoot"/> refuses underneath, and why the guard here is kept: it
    /// says which root disagreed before the claim is asked at all.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A run root has already been claimed, and it is a different volume than the one named now.
    /// </exception>
    public static void Configure(string volumeRoot)
    {
        if (_runRoot is not null)
        {
            if (!string.Equals(_runRoot.Parent, volumeRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"This run already works on {_runRoot.Parent}, and --scratch now names {volumeRoot}. One process is one run on one volume.");
            }

            return;
        }

        _runRoot = ScratchDirectory.ClaimRunRoot(volumeRoot);
    }

    /// <summary>
    /// Removes the directory this run claimed, along with everything the suite put in it.
    /// </summary>
    /// <remarks>
    /// The whole tree rather than each part of it, so a run that failed half way through leaves nothing in
    /// the volume root and the next run claims a fresh name rather than inheriting this one's leftovers.
    /// </remarks>
    public static void ReleaseRunRoot()
    {
        var runRoot = _runRoot;

        _runRoot = null;

        runRoot?.Remove();
    }

    /// <summary>
    /// Prepares <see cref="LongDirectoryRoot"/> for use, empty, and gives it back.
    /// </summary>
    /// <remarks>
    /// Recreating it is a recursive delete of exactly the tree this run made there, which is a known shape.
    /// The path is one this run claimed rather than one written into the source, so the delete has nothing to
    /// trust but itself.
    /// </remarks>
    public static string RecreateLongDirectoryRoot()
    {
        var path = LongDirectoryRoot;

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        if (!Directory.CreateDirectory(path).Exists)
        {
            throw new IOException($"Could not create the long-directory root {path}");
        }

        return path;
    }

    /// <summary>
    /// The path of a named child of this run's directory, whether or not it exists.
    /// </summary>
    /// <param name="purpose">What the directory is for, such as <c>scratch</c> or <c>long</c>.</param>
    /// <returns>The full path, with a trailing separator.</returns>
    private static string PathUnder(string purpose)
    {
        var runRoot = _runRoot ?? throw new InvalidOperationException(
            $"{nameof(Configure)} has not run, so there is no directory to take '{purpose}' from.");

        return Path.Combine(runRoot.FullPath, purpose) + Path.DirectorySeparatorChar;
    }

    private bool _disposed;

    /// <summary>
    /// Deletes any leftover contents and recreates an empty scratch root.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The scratch root's volume does not read as recycling. Recycling anything there depends on it, so
    /// every case would fail for one missing registry key.
    /// </exception>
    public void Recreate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        RequireRecyclableVolume();

        DeleteTree();

        if (!Directory.CreateDirectory(Root).Exists)
        {
            throw new IOException($"Could not create scratch root {Root}");
        }
    }

    /// <summary>
    /// Fails unless <see cref="Root"/>'s volume is one the library will hand to the Shell.
    /// </summary>
    /// <remarks>
    /// The gate refuses a path whose volume is not <see cref="RecycleBinAvailabilityReason.Enabled"/>, and
    /// the default <see cref="NoRecycleBinAction"/> refuses too, so a volume without its per-volume key
    /// would turn most of the suite red at once with failures that name the Recycle Bin rather than the
    /// missing key. Read before anything is deleted, so a machine that cannot run the suite loses nothing
    /// first.
    /// </remarks>
    private static void RequireRecyclableVolume()
    {
        // Inspect answers about a volume root, and the scratch root is a directory inside one. Handing
        // it the directory would be answered VolumeNotFound, which says nothing about recycling.
        var volumeRoot = Path.GetPathRoot(Root)!;
        var reason = RecycleBinVolumeState.Inspect(volumeRoot).Reason;

        if (reason != RecycleBinAvailabilityReason.Enabled)
        {
            throw new InvalidOperationException(
                $"The scratch volume {volumeRoot} reads as {reason}, so nothing the suite recycles " +
                "there can be handed to the Shell. This suite needs the volume's Recycle Bin setting recorded " +
                @"under HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{GUID}\NukeOnDelete=0, " +
                "which the Recycle Bin properties dialog writes when 'Don't move files to the Recycle Bin' is " +
                "clear for that drive.");
        }
    }

    /// <summary>
    /// Creates generated files in the scratch root, named <c>(1).jpg</c> upward. The parenthesised names
    /// and the image extension are kept from the sample files this suite used to commit, because a Shell
    /// rename has to cope with exactly those names.
    /// </summary>
    /// <param name="count">
    /// How many to create, starting at <c>(1).jpg</c>. Defaults to the whole standard set.
    /// </param>
    /// <returns>Full paths of the created files, in name order.</returns>
    public IReadOnlyList<string> CreateFixtureSet(int count = FixtureFileCount)
    {
        var created = new List<string>(count);

        for (var index = 1; index <= count; index++)
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
        // behind, where a workspace file listing trips over paths it cannot enumerate. LongPathStager's
        // own spelling of the prefix is used rather than a second copy of it here, because that one also
        // knows a share is written \\?\UNC\... and not \\?\\server\share\... -- the second is a path that
        // begins with a separator, resolves against the current drive, and would raise
        // DirectoryNotFoundException, which Remove reads as "already gone". The run would then leave its
        // whole tree on the share it was pointed at and report success.
        var root = LongPathStager.PathForIo(ScratchWorkspace.Root.TrimEnd(Path.DirectorySeparatorChar));

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

}
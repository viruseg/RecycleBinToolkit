using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// The single scratch directory every test writes into.
/// </summary>
/// <remarks>
/// Its location is not decided here: <c>--scratch</c> names the volume, and <see cref="Configure"/> claims a
/// directory of this run's own inside that volume. Nothing in the source names a path, so the suite cannot be
/// pointed twice at the same directory and cannot delete a directory it did not create.
/// <para>
/// Almost every case recycles a path on <see cref="Root"/>'s volume, and which of them can do so is a
/// property of the machine rather than of this file: <see cref="Reason"/> asks the library once what the
/// named volume is, and a case that needs a volume of one kind is expected to read that answer rather than
/// manufacture one. Nothing here refuses a volume the caller named, so a run on a flash drive, a share, or a
/// machine whose policy turns the bin off starts instead of dying before the first case reports;
/// <see cref="RequireNonRecycling"/> is how a case says it did not run here.
/// </para>
/// </remarks>
internal sealed class ScratchWorkspace : IDisposable
{
    private static ScratchDirectory? _runRoot;

    private static RecycleBinAvailabilityReason? _reason;

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
    /// works on this string rather than on a drive letter written into the source, which is what lets the
    /// whole suite run on a volume the caller named.
    /// </summary>
    public static string VolumeRoot => _runRoot?.Parent ?? throw new InvalidOperationException(
        $"{nameof(Configure)} has not run, so no volume has been named. Pass --scratch <root> on the command line.");

    /// <summary>
    /// What the library reports about <see cref="VolumeRoot"/>: whether that volume uses a Recycle Bin and,
    /// when it does not, why not. Asked once per run, so every case and every message names the same thing.
    /// </summary>
    /// <remarks>
    /// What this is asked is the machine's own reading of the volume, through
    /// <see cref="RecycleBinVolumeState.Inspect"/>, so a case that needs a volume of one kind reads it and
    /// skips or runs on it rather than deciding for itself.
    /// Windows records recycling per volume under
    /// <c>BitBucket\Volume\{GUID}\NukeOnDelete=0</c>, which is what
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/> rests on; a volume with no such key answers
    /// <see cref="RecycleBinAvailabilityReason.Indeterminate"/> rather than enabled. So a run on a
    /// flash drive, a share, or a machine whose policy turns the bin off is a run that works, and a case
    /// that cannot make its claim there is expected to say so through
    /// <see cref="RequireNonRecycling"/> rather than fail on it.
    /// <para>
    /// That a rule about the path could not produce the same answers is witnessed where the two answers
    /// differ, on a volume with no Recycle Bin — the bin-less run, not this one. What a run on a healthy
    /// volume can witness is that the cached value still agrees with the library, which is what
    /// <c>VolumeSuite.TheScratchVolumeReasonIsWhatTheLibraryReports</c> asserts and all it asserts.
    /// </para>
    /// <list type="bullet">
    /// <item>Cached for the life of the claim because the answer does not change under a run and the registry
    /// reads are on a path the case count does not justify repeating. What keeps the cache from answering
    /// about a volume the run has left is an invariant: this value is written in one place, that place reads
    /// <see cref="VolumeRoot"/>, and <see cref="VolumeRoot"/> throws while no run root is held.</item>
    /// <item>A second <see cref="Configure"/> cannot bring in a different volume either way. It returns or
    /// throws for every state in which a run root is already held. One volume per process, and with it one
    /// answer.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><see cref="Configure"/> has not run.</exception>
    public static RecycleBinAvailabilityReason Reason => _reason ??= RecycleBinVolumeState.Inspect(VolumeRoot).Reason;

    /// <summary>
    /// Ends the calling case as not run when <see cref="Reason"/> is
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/>.
    /// </summary>
    /// <param name="because">
    /// What the case needed from the volume, as a noun phrase the skip line can carry: it completes
    /// "This case needs ___". Carried into that line because it is the only record left of a case that
    /// produced no coverage.
    /// </param>
    /// <remarks>
    /// Its counterpart is <see cref="RequireRecycling"/>. A run on a volume with no Recycle Bin is a run that
    /// works: the cases about a bin-less volume run and the cases about recycling say they did not. Neither
    /// guard refuses the volume — they refuse the case, and only when the volume cannot answer what the case
    /// asked. The volume itself is always usable; which cases can say anything about it is what varies.
    /// </remarks>
    /// <exception cref="CheckSkippedException">
    /// The volume does use a Recycle Bin. The message names the volume and <paramref name="because"/> and
    /// reads as a fact about this machine, not as a fault in it.
    /// </exception>
    public static void RequireNonRecycling(string because)
    {
        if (Reason == RecycleBinAvailabilityReason.Enabled)
        {
            Check.Skip($"{VolumeRoot} has a Recycle Bin. This case needs {because}");
        }
    }

    /// <summary>
    /// Ends the calling case as not run unless <see cref="Reason"/> is
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/>.
    /// </summary>
    /// <param name="because">
    /// What the case needed from the volume, as a noun phrase the skip line can carry: it completes
    /// "This case needs ___". Carried into that line because it is the only record left of a case that
    /// produced no coverage.
    /// </param>
    /// <remarks>
    /// A case that recycles a file and reads the bin back cannot produce its answer on a volume with no bin:
    /// the file is refused before anything is attempted, and the counter has nothing to count. Without this
    /// guard such a case goes red on a flash drive or a share, and a run of forty identical failures buries
    /// the cases that did prove something. A skip is the honest reading there — the volume could not answer
    /// the question, so the case did not run — and it leaves the run's exit code describing coverage that
    /// exists rather than a wall of red.
    /// </remarks>
    /// <exception cref="CheckSkippedException">
    /// The volume does not use a Recycle Bin. The message names the volume, <paramref name="because"/> and the
    /// reason the library gave, and reads as a fact about this machine.
    /// </exception>
    public static void RequireRecycling(string because)
    {
        if (Reason != RecycleBinAvailabilityReason.Enabled)
        {
            Check.Skip($"{VolumeRoot} has no Recycle Bin ({Reason}). This case needs {because}");
        }
    }

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
    /// Absolute path of the root for cases that build <em>directories</em> past the
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
    /// <para>
    /// Claiming also clears the cached <see cref="Reason"/>. Naming the same volume again is one run, so
    /// that path keeps the answer already read for it, and on the path that does claim there is nothing to
    /// clear — no run root held means no answer could have been read, because reading one needs
    /// <see cref="VolumeRoot"/>. The line is there so that stays true of this method rather than of a
    /// property of every caller, and a refactor that gave <see cref="Reason"/> a source other than the
    /// machine would otherwise leave a stale value behind it.
    /// </para>
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
        _reason = null;
    }

    /// <summary>
    /// Removes the directory this run claimed, along with everything the suite put in it.
    /// </summary>
    /// <remarks>
    /// The whole tree rather than each part of it, so a run that failed half way through leaves nothing in
    /// the volume root and the next run claims a fresh name rather than inheriting this one's leftovers.
    /// <para>
    /// The cached <see cref="Reason"/> is dropped here alongside the run root it was read about, though
    /// dropping the run root alone would already make it unreadable. The line stays for the same reason it
    /// stays in <see cref="Configure"/>: a value the machine could no longer contradict should not be sitting
    /// in a field waiting for the run root to come back.
    /// </para>
    /// </remarks>
    public static void ReleaseRunRoot()
    {
        var runRoot = _runRoot;

        _runRoot = null;
        _reason = null;

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
    /// <remarks>
    /// Any volume the caller named is prepared, whatever <see cref="Reason"/> says about it. A volume that
    /// cannot recycle is not a broken machine, so a case that cannot make its claim there reports itself as
    /// unrun through <see cref="RequireNonRecycling"/> rather than the run dying here before a single case
    /// has said anything.
    /// </remarks>
    /// <exception cref="IOException">
    /// The scratch root could not be created, or could not be emptied because something still holds a file in it.
    /// </exception>
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
    /// Creates generated files in the scratch root, named <c>(1).jpg</c> upward.
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
        // behind, where a workspace file listing trips over paths it cannot enumerate.
        var root = ExtendedPath.For(ScratchWorkspace.Root.TrimEnd(Path.DirectorySeparatorChar));

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
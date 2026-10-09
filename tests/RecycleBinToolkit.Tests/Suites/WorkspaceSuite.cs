using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

internal static class WorkspaceSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("WorkspaceSuite." + nameof(WorkspaceCanBeRecreatedAndPopulated), WorkspaceCanBeRecreatedAndPopulated),
        new TestCase("WorkspaceSuite." + nameof(TheBackendFlagNamesAKnownBackend), TheBackendFlagNamesAKnownBackend),
    ];

    /// <summary>
    /// The flag that decides which Recycle Bin path this run measures, read the same way whatever it is
    /// spelled like.
    /// </summary>
    /// <remarks>
    /// A run that silently measured the wrong path would report every assertion it made and none of them
    /// about what its command line said, which is the failure this exists to prevent. So the default is
    /// pinned by name — the Shell path, because that is what every run before this flag existed took — and
    /// every spelling of a known backend is accepted while everything else is refused with a message.
    /// <para>
    /// A flag followed by another flag is refused the way <c>--scratch</c> refuses one. Taking the next token
    /// either way would read <c>--backend --load</c> as a backend named <c>--load</c>, and a run would measure
    /// the Shell path while its command line said it asked for something else.
    /// </para>
    /// </remarks>
    private static Task TheBackendFlagNamesAKnownBackend(TestContext context)
    {
        Check.Equal(
            TestBackendKind.WinApi,
            TestBackend.Read([], out _),
            "a run that names no backend measures the Shell path, which is what every run before this flag took");

        foreach (var spelling in new[] { "winapi", "WinApi", "WINAPI", "native", "Native", "NATIVE" })
        {
            TestBackend.Read(["--backend", spelling], out var accepted);

            Check.Equal(string.Empty, accepted, $"--backend {spelling} names a backend this library has");
        }

        foreach (var bad in new[] { string.Empty, "   ", "shell", "com", "win32", "native2", "1" })
        {
            TestBackend.Read(["--backend", bad], out var problem);

            Check.True(problem.Length > 0, $"--backend \"{bad}\" is refused with a message rather than guessed at");
        }

        TestBackend.Read(["--backend", "--load"], out var flagInsteadOfValue);

        Check.True(flagInsteadOfValue.Length > 0, "a flag after --backend is refused as a value, the way --scratch refuses one");

        TestBackend.Read(["--backend"], out var nothingAfter);

        Check.True(nothingAfter.Length > 0, "and --backend with nothing after it at all is refused too");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The scratch root is a directory under this run's own directory, so <see cref="ScratchWorkspace.Dispose"/>
    /// empties it without touching the run around it — and the run around it is what every other case writes
    /// under. The two are separate lifetimes, and a single <c>Dispose</c> that took both would leave the next
    /// case with no volume to work on.
    /// </summary>
    private static Task WorkspaceCanBeRecreatedAndPopulated(TestContext context)
    {
        var runRoot = ScratchWorkspace.RunRoot;
        var workspace = new ScratchWorkspace();
        try
        {
            workspace.Recreate();
            Check.True(Directory.Exists(ScratchWorkspace.Root), $"scratch root exists: {ScratchWorkspace.Root}");
            Check.True(ParentOf(ScratchWorkspace.Root) == Trimmed(runRoot), $"and it sits under this run's directory: {runRoot}");

            var created = workspace.CreateFixtureSet();
            Check.Equal(ScratchWorkspace.FixtureFileCount, created.Count, "generated file count matches the declared count");

            foreach (var path in created)
            {
                Check.True(File.Exists(path), $"generated file exists: {path}");
                Check.True(new FileInfo(path).Length == ScratchWorkspace.FixtureFileSizeBytes, $"generated file has the declared size: {path}");
            }

            workspace.Dispose();
            Check.False(Directory.Exists(ScratchWorkspace.Root), "scratch root removed after Dispose");
            Check.True(Directory.Exists(runRoot), "and the run's own directory survives it, because the next case writes under it too");
        }
        finally
        {
            workspace.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The directory a path sits in, spelled the way the filesystem spells it.
    /// </summary>
    private static string ParentOf(string path) =>
        Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar))?.FullName ?? path;

    /// <summary>
    /// A path without its trailing separator. The run root carries one and <see cref="ParentOf"/> does not,
    /// and comparing the two spellings of one directory is how a test would report a difference that is
    /// only a separator.
    /// </summary>
    private static string Trimmed(string path) => path.TrimEnd(Path.DirectorySeparatorChar);
}
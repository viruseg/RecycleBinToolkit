using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

internal static class WorkspaceSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("WorkspaceSuite." + nameof(WorkspaceCanBeRecreatedAndPopulated), WorkspaceCanBeRecreatedAndPopulated),
        new TestCase("WorkspaceSuite." + nameof(TheRemovedBackendFlagIsRefused), TheRemovedBackendFlagIsRefused),
    ];

    /// <summary>
    /// The <c>--backend</c> flag chose which Recycle Bin path a run measured. There is one path now,
    /// so the flag is refused rather than ignored: a run that quietly accepted it would report coverage
    /// nobody asked for under a command line nobody wrote.
    /// </summary>
    private static async Task TheRemovedBackendFlagIsRefused(TestContext context)
    {
        Check.True(TestRunner.IsRemovedFlagPresent(["--backend", "native"]), "--backend native is refused");
        Check.True(TestRunner.IsRemovedFlagPresent(["--backend"]), "a bare --backend is refused too");
        Check.True(
            TestRunner.IsRemovedFlagPresent(["--scratch", @"W:\", "--backend", "winapi"]),
            "--backend anywhere on the command line is refused");
        Check.False(TestRunner.IsRemovedFlagPresent([]), "no flag is no refusal");
        Check.False(TestRunner.IsRemovedFlagPresent(["--filter", "EmptySuite"]), "other flags are not refusals");

        Check.Equal(
            2,
            await TestRunner.RunAsync(["--backend", "native"], []),
            "a run naming the removed flag exits 2 without needing a volume");
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
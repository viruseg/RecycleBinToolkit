using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Suites;

internal static class WorkspaceSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("WorkspaceSuite." + nameof(WorkspaceCanBeRecreatedAndPopulated), WorkspaceCanBeRecreatedAndPopulated),
    ];

    private static Task WorkspaceCanBeRecreatedAndPopulated(TestContext context)
    {
        var workspace = new ScratchWorkspace();
        try
        {
            workspace.Recreate();
            Check.True(Directory.Exists(ScratchWorkspace.Root), $"scratch root exists: {ScratchWorkspace.Root}");

            var created = workspace.CreateFixtureSet();
            Check.Equal(ScratchWorkspace.FixtureFileCount, created.Count, "generated file count matches the declared count");

            foreach (var path in created)
            {
                Check.True(File.Exists(path), $"generated file exists: {path}");
                Check.True(new FileInfo(path).Length == ScratchWorkspace.FixtureFileSizeBytes, $"generated file has the declared size: {path}");
            }

            workspace.Dispose();
            Check.False(Directory.Exists(ScratchWorkspace.Root), "scratch root removed after Dispose");
        }
        finally
        {
            workspace.Dispose();
        }

        return Task.CompletedTask;
    }
}
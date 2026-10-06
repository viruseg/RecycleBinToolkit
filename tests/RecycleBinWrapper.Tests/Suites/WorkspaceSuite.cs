using RecycleBinWrapper.Tests.Host;
using RecycleBinWrapper.Tests.Infrastructure;

namespace RecycleBinWrapper.Tests.Suites;

internal static class WorkspaceSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase(nameof(WorkspaceCanBeRecreatedAndPopulated), WorkspaceCanBeRecreatedAndPopulated),
    ];

    private static Task WorkspaceCanBeRecreatedAndPopulated(TestContext context)
    {
        var workspace = new ScratchWorkspace();
        try
        {
            workspace.Recreate();
            Check.True(Directory.Exists(ScratchWorkspace.Root), $"scratch root exists: {ScratchWorkspace.Root}");

            var copied = workspace.CopyAllFixtures();
            Check.Equal(FixtureFiles.All().Count, copied.Count, "copied file count matches fixture count");

            foreach (var path in copied)
            {
                Check.True(File.Exists(path), $"copied file exists: {path}");
                Check.True(new FileInfo(path).Length > 0, $"copied file is non-empty: {path}");
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
namespace RecycleBinWrapper.Tests.Infrastructure;

/// <summary>
/// Read-only access to the committed fixture files. The fixture directory is never written to.
/// </summary>
internal static class FixtureFiles
{
    private const string SourceDirectoryName = "Test files";

    /// <summary>
    /// Absolute path of the committed fixture directory, located by walking up from the test
    /// binary until a directory with that name is found.
    /// </summary>
    public static string SourceDirectory { get; } = ResolveSourceDirectory();

    /// <summary>
    /// Full paths of every fixture file, ordered so runs are reproducible.
    /// </summary>
    public static IReadOnlyList<string> All() =>
        Directory.GetFiles(SourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string ResolveSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, SourceDirectoryName);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not find the '{SourceDirectoryName}' directory in any parent of {AppContext.BaseDirectory}.");
    }
}
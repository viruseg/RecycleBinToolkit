using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Infrastructure;
using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Suites;

internal static class RestoreSuite
{
    internal static IReadOnlyList<TestCase> Cases =>
    [
        new TestCase("RestoreSuite." + nameof(DefaultsAreTheDocumentedOnes), DefaultsAreTheDocumentedOnes)
    ];

    /// <summary>
    /// The shapes a restore reports in are public and have no behaviour behind them yet, so what this
    /// pins is every value a caller can already see: the two conflict policies and their defaults, the
    /// numbers behind the two restore stages, and what a result carries back.
    /// </summary>
    /// <remarks>
    /// The normalisation half is the one with teeth. <see cref="RecycleBinOptions.Normalize"/> copies
    /// field by field, so an option it does not name is silently reset to the property initialiser's
    /// default on every internal call — a caller who asked for one behaviour would get another, with
    /// nothing raised and nothing reported.
    /// <para>
    /// The result half is read list by list rather than through its totals, because a constructor that
    /// swapped two of them would answer every count correctly. <see cref="RecycleBinRestoreResult.Succeeded"/>
    /// is read on both of its branches: a conflict alone must leave it true, which is what the remarks on
    /// it promise.
    /// </para>
    /// </remarks>
    private static Task DefaultsAreTheDocumentedOnes(TestContext context)
    {
        using var workspace = new ScratchWorkspace();
        workspace.Recreate();

        Check.Equal(RestoreDirectoryConflict.MergeSkip, RecycleBinOptions.Default.DirectoryConflict, "the directory default");
        Check.Equal(RestoreFileConflict.Skip, RecycleBinOptions.Default.FileConflict, "the file default");

        // Normalize copies fields one by one; a field it does not mention is dropped on every internal call.
        var normalized = new RecycleBinOptions
        {
            DirectoryConflict = RestoreDirectoryConflict.MergeReplace,
            FileConflict = RestoreFileConflict.Rename
        }.Normalize();

        Check.Equal(RestoreDirectoryConflict.MergeReplace, normalized.DirectoryConflict, "directory policy survives normalization");
        Check.Equal(RestoreFileConflict.Rename, normalized.FileConflict, "file policy survives normalization");

        Check.Equal(10, (int)RecycleFailureStage.RestoreFailed, "RestoreFailed keeps clear of the removed Shell stages");
        Check.Equal(11, (int)RecycleFailureStage.RestoreConflictRefused, "RestoreConflictRefused follows it");

        var conflict = new RecycleRestoreConflict("b", true, RecycleRestoreConflictAction.Skipped);
        var result = new RecycleBinRestoreResult(["a"], [conflict], [], 1, 42, TimeSpan.FromMilliseconds(3));
        Check.Equal(1, result.ItemsRestored, "one item restored");
        Check.Equal(42, result.RestoredSize, "its bytes");
        Check.Equal(1, result.RestoredPaths.Count, "the restored path is carried");
        Check.Equal("a", result.RestoredPaths[0], "and it is the one handed in, not a path invented here");
        Check.Equal(1, result.Conflicts.Count, "the conflict is carried");
        Check.True(ReferenceEquals(conflict, result.Conflicts[0]), "and it is the one handed in");
        Check.Equal(0, result.Failures.Count, "the failures list is carried, empty");
        Check.True(result.Succeeded, "nothing failed");
        Check.Equal(3, result.Elapsed.TotalMilliseconds, "elapsed carries");

        // A conflict alone does not make the result fail, so the false branch is only reachable through
        // a failure — and through any of them, whatever the reason recorded on it.
        var failed = new RecycleBinRestoreResult([], [conflict], [new RecycleFailure("c", 0, 1, RecycleFailureStage.RestoreFailed)], 0, 0, TimeSpan.Zero);
        Check.False(failed.Succeeded, "one failure makes the result not have succeeded");
        Check.Equal(1, failed.Failures.Count, "and the failure is carried");

        workspace.Dispose();

        return Task.CompletedTask;
    }
}
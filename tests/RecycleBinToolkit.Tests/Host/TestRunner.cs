using System.Diagnostics;

namespace RecycleBinToolkit.Tests.Host;

/// <summary>
/// Runs registered test cases and reports the result.
/// </summary>
internal sealed class TestRunner
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs the cases selected by the command line.
    /// </summary>
    /// <param name="args">
    /// <c>--load</c> selects the load group; without it the regular group is selected. <c>--all</c> and
    /// no arguments select every regular case, <c>--filter &lt;substring&gt;</c> narrows whichever group
    /// was selected to the cases whose name contains the substring. The groups never overlap: no argument
    /// other than <c>--load</c> runs a load case.
    /// </param>
    /// <param name="cases">All known cases; grouping and filtering both happen inside the runner.</param>
    /// <returns>0 when every selected case passed, 1 otherwise.</returns>
    internal static async Task<int> RunAsync(string[] args, IReadOnlyList<TestCase> cases)
    {
        var filter = ParseFilter(args);
        var kind = args.Contains("--load", StringComparer.Ordinal) ? TestKind.Load : TestKind.Regular;

        var selected = Select(cases, kind, filter);

        if (selected.Length == 0)
        {
            Console.Error.WriteLine(DescribeEmptySelection(cases, kind, filter));
            return 1;
        }

        Console.WriteLine($"running {selected.Length} of {cases.Count} test(s)");

        // Left out silently, a load case reads as "everything passed" — which is exactly the wrong
        // conclusion to draw about a case nobody asked for, and it is the only way to tell afterwards
        // that a run never reached it.
        var deferred = Select(cases, TestKind.Load, filter).Length;
        if (kind is TestKind.Regular && deferred > 0)
        {
            Console.WriteLine($"        {deferred} load test(s) match and are not part of this run; pass --load to include them");
        }

        Console.WriteLine();

        var passed = 0;
        List<string>? failures = null;

        foreach (var testCase in selected)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TestBudget);
                await testCase.Body(new TestContext(cts.Token)).ConfigureAwait(false);
                stopwatch.Stop();
                passed++;
                Console.WriteLine($"  PASS  {testCase.Name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                (failures ??= []).Add(testCase.Name);
                Console.WriteLine($"  FAIL  {testCase.Name} ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");

                if (ex is not CheckFailedException)
                {
                    Console.WriteLine($"        {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "        ")}");
                }
            }
        }

        Console.WriteLine();
        var failedCount = selected.Length - passed;
        Console.WriteLine(failedCount == 0
            ? $"all {passed} test(s) passed"
            : $"{passed} passed, {failedCount} failed");

        if (failures is not null)
        {
            Console.WriteLine("failed: " + string.Join(", ", failures));
        }

        return failedCount == 0 ? 0 : 1;
    }

    private static TestCase[] Select(IReadOnlyList<TestCase> cases, TestKind kind, string? filter) =>
        [.. cases.Where(c => c.Kind == kind && (filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))];

    private static string DescribeEmptySelection(IReadOnlyList<TestCase> cases, TestKind kind, string? filter)
    {
        if (filter is null)
        {
            return kind is TestKind.Load
                ? "no load test cases registered"
                : "no test cases registered";
        }

        // A filter that names only the group this run did not ask for is the likeliest reason for an
        // empty selection, and "no test case matches" sends the reader hunting for a typo in the name
        // instead of for the argument that would have run them.
        var other = kind is TestKind.Load ? TestKind.Regular : TestKind.Load;
        var deferred = Select(cases, other, filter).Length;

        return deferred == 0
            ? $"no test case matches '{filter}'"
            : other is TestKind.Load
                ? $"'{filter}' matches {deferred} load test(s); pass --load to run them"
                : $"'{filter}' matches {deferred} regular test(s), which --load does not select";
    }

    private static string? ParseFilter(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--filter" or "-f" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
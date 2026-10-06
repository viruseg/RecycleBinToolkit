using System.Diagnostics;

namespace RecycleBinWrapper.Tests.Host;

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
    /// <c>--all</c> or no arguments runs everything; <c>--filter &lt;substring&gt;</c> runs the
    /// cases whose name contains the substring.
    /// </param>
    /// <param name="cases">All known cases; filtering happens inside the runner.</param>
    /// <returns>0 when every selected case passed, 1 otherwise.</returns>
    internal static async Task<int> RunAsync(string[] args, IReadOnlyList<TestCase> cases)
    {
        var filter = ParseFilter(args);
        var selected = cases.Where(c => filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (selected.Length == 0)
        {
            Console.Error.WriteLine(filter is null
                ? "no test cases registered"
                : $"no test case matches '{filter}'");
            return 1;
        }

        Console.WriteLine($"running {selected.Length} of {cases.Count} test(s)");
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
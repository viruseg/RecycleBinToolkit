using System.Diagnostics;
using RecycleBinToolkit.Tests.Infrastructure;

namespace RecycleBinToolkit.Tests.Host;

/// <summary>
/// Runs registered test cases and reports the result.
/// </summary>
internal sealed class TestRunner
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Every group, in the order a reader is most likely to have meant one when a filter matches only
    /// some of them.
    /// </summary>
    private static readonly TestKind[] Groups = [TestKind.Regular, TestKind.Load];

    /// <summary>
    /// Runs the cases selected by the command line.
    /// </summary>
    /// <param name="args">
    /// <c>--scratch &lt;root&gt;</c> names the volume every case works on and is required. <c>--load</c>
    /// selects the load group; without it, the regular group is selected. <c>--all</c> and no arguments
    /// select every regular case, <c>--filter &lt;substring&gt;</c> narrows whichever group was selected to
    /// the cases whose name contains the substring, and <c>--exclude &lt;substring&gt;</c> takes the cases
    /// whose name contains it back out of that selection. The groups never overlap: no argument other than
    /// <c>--load</c> runs a load case.
    /// </param>
    /// <param name="cases">All known cases; grouping, filtering and exclusion all happen inside the runner.</param>
    /// <returns>
    /// 0 when at least one case ran and passed. 1 when one failed, when the selection was empty, or
    /// when every selected case was skipped -- a run that produced no coverage is not a green run.
    /// 2 when <c>--scratch</c> was left out or was given no root, when <c>--filter</c> or
    /// <c>--exclude</c> was given nothing to match, or when <c>--exclude</c> was given no
    /// <c>--all</c>, <c>--filter</c> or <c>--load</c> to narrow.
    /// </returns>
    internal static async Task<int> RunAsync(string[] args, IReadOnlyList<TestCase> cases)
    {
        if (IsRemovedFlagPresent(args))
        {
            Console.Error.WriteLine(
                "--backend chose which Recycle Bin path a run measured. There is one path now, so the flag " +
                "was removed rather than kept as a silent no-op: a run that accepted it would report coverage " +
                "its command line did not ask for.");
            return 2;
        }

        if (!TryParseScratchRoot(args, out var scratchRoot, out var scratchProblem))
        {
            Console.Error.WriteLine(
                "--scratch takes the root of a volume to test on, such as --scratch D:\\ or --scratch \\\\server\\share\\. It is " +
                "required rather than defaulted, because every case writes files and removes directories: a run that guessed the " +
                "volume would be a run whose cleanup nobody chose. The suite creates a directory of its own inside the root you " +
                "name, so the path has to be the volume root and not a directory in it. A command line that cannot carry two " +
                "leading backslashes may name a share as //server/share/ instead, which is read as the same volume.");
            Console.Error.WriteLine($"        {scratchProblem}");
            return 2;
        }

        if (!TryParseFilter(args, out var filter, out var filterProblem))
        {
            Console.Error.WriteLine(
                "--filter takes the substring a test name must contain, such as --filter LongPathSuite or -f Empty. " +
                "A flag after it is read as a missing substring rather than as one, and so is a --filter with " +
                "nothing after it at all -- a narrowing flag that quietly does not narrow is a run nobody asked for.");
            Console.Error.WriteLine($"        {filterProblem}");
            return 2;
        }

        var group = SelectGroup(args);

        if (!TryParseExclude(args, out var exclude, out var excludeProblem))
        {
            Console.Error.WriteLine(
                "--exclude takes the substring a test name must NOT contain, such as --all --exclude EmptySuite " +
                "or --filter Restore -x Long, so a run that must not empty the Recycle Bin can say so. A flag " +
                "after it is read as a missing substring rather than as one, and so is an --exclude with nothing " +
                "after it at all -- an exclusion that quietly keeps every case is a run nobody asked for.");
            Console.Error.WriteLine($"        {excludeProblem}");
            return 2;
        }

        if (exclude is not null && !NamesWhatItRuns(args, group, filter))
        {
            Console.Error.WriteLine(
                "--exclude takes cases out of a run that --all, --filter or --load named, so pass one of them beside " +
                "it. On its own it would rewrite the default run -- which is every regular case -- rather than narrow " +
                "a selection somebody made.");
            return 2;
        }

        // The run's own directory is claimed from here on, so everything below runs inside the try whose
        // finally removes it — including the empty-selection return, which is an exit path like any other.
        // A claim is created in the volume root, and one left there is a directory this suite made that
        // nothing owns.
        try
        {
            ScratchWorkspace.Configure(scratchRoot);

            var selected = Select(cases, group, filter, exclude);

            if (selected.Length == 0)
            {
                Console.Error.WriteLine(DescribeEmptySelection(cases, group, filter, exclude));
                return 1;
            }

            Console.WriteLine($"scratch root {ScratchWorkspace.RunRoot}");
            Console.WriteLine($"running {selected.Length} of {cases.Count} test(s)");

            DescribeExcluded(cases, group, filter, exclude);
            DescribeDeferred(cases, group, filter);

            Console.WriteLine();

            // Everything below writes files and removes directories, so the run's own tree goes with it
            // rather than being left in the volume root for the next run to trip over. A run that fails
            // still cleans up: the cases report through `selected`, not through what is left on disk.
            return await ExecuteAsync(selected).ConfigureAwait(false);
        }
        finally
        {
            ScratchWorkspace.ReleaseRunRoot();
        }
    }

    /// <summary>
    /// Runs the selected cases in order and reports what they did.
    /// </summary>
    /// <param name="selected">The cases to run, in the order they were selected.</param>
    /// <returns>
    /// 0 when at least one case passed and none failed. 1 when one failed, or when every case was
    /// skipped -- a run that produced no coverage is not a green run, the same shape an empty selection
    /// already returns 1 for.
    /// </returns>
    private static async Task<int> ExecuteAsync(TestCase[] selected)
    {
        var passed = 0;
        var skipped = 0;
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
            catch (CheckSkippedException skip)
            {
                // Counted apart from both outcomes. A case that cannot run on this machine produced no
                // coverage, and a run that calls it a pass says otherwise -- which is the failure the
                // deferral lines exist to prevent, applied to a case rather than to a group.
                stopwatch.Stop();
                skipped++;
                Console.WriteLine($"  SKIP  {testCase.Name}");
                Console.WriteLine($"        {skip.Message}");
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
        var failedCount = selected.Length - passed - skipped;
        Console.WriteLine(Summary(passed, skipped, failedCount));

        if (failures is not null)
        {
            Console.WriteLine("failed: " + string.Join(", ", failures));
        }

        return failedCount == 0 && passed > 0 ? 0 : 1;
    }

    /// <summary>
    /// The tally line, naming a skip rather than folding it into either outcome.
    /// </summary>
    private static string Summary(int passed, int skipped, int failed) => (passed, skipped, failed) switch
    {
        (0, 0, _) => $"all {passed + skipped + failed} test(s) ran and none of them passed",
        (_, 0, 0) => $"all {passed} test(s) passed",
        (_, 0, _) => $"{passed} passed, {failed} failed",
        (_, > 0, 0) => $"{passed} passed, {skipped} skipped (no failures)",
        _ => $"{passed} passed, {skipped} skipped, {failed} failed"
    };

    /// <summary>Which group the command line asks for.</summary>
    private static TestKind SelectGroup(string[] args) =>
        args.Contains("--load", StringComparer.Ordinal) ? TestKind.Load : TestKind.Regular;

    /// <summary>
    /// Names the groups this run left out, one line each.
    /// </summary>
    /// <remarks>
    /// Left out silently, a case reads as "everything passed" — which is exactly the wrong conclusion to
    /// draw about a case nobody asked for, and it is the only way to tell afterwards that a run never
    /// reached it. Only the regular run reports a deferral: the load group is asked for by name, so what
    /// it leaves out is the caller's own choice rather than a group nobody was offered. An exclusion is
    /// not applied to this count either, for the reason <see cref="DescribeEmptySelection"/> gives.
    /// </remarks>
    private static void DescribeDeferred(IReadOnlyList<TestCase> cases, TestKind kind, string? filter)
    {
        if (kind is not TestKind.Regular)
        {
            return;
        }

        foreach (var group in Groups)
        {
            if (group == kind)
            {
                continue;
            }

            var deferred = Select(cases, group, filter, exclude: null).Length;
            if (deferred > 0)
            {
                Console.WriteLine($"        {deferred} {Name(group)} test(s) match and are not part of this run; {Advice(group)}");
            }
        }
    }

    /// <summary>
    /// Names the cases the exclusion took out of the group this run selected.
    /// </summary>
    /// <remarks>
    /// A count lower than the one <c>--all</c> promises reads as something having been skipped when nothing
    /// was: those cases were never selected, and this is the only line that says which substring left them out.
    /// It is written even when the substring matched nothing, which is what tells a reader the name was wrong
    /// rather than that the flag did nothing.
    /// </remarks>
    private static void DescribeExcluded(IReadOnlyList<TestCase> cases, TestKind kind, string? filter, string? exclude)
    {
        if (exclude is null)
        {
            return;
        }

        var excluded = Select(cases, kind, filter, exclude: null).Length - Select(cases, kind, filter, exclude).Length;
        Console.WriteLine($"        {excluded} {Name(kind)} test(s) are left out because the name contains '{exclude}'");
    }

    private static TestCase[] Select(IReadOnlyList<TestCase> cases, TestKind kind, string? filter, string? exclude) =>
        [.. cases.Where(c => c.Kind == kind
            && (filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            && (exclude is null || !c.Name.Contains(exclude, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>
    /// Whether the command line named which cases to run, which is the only thing an exclusion can narrow.
    /// </summary>
    private static bool NamesWhatItRuns(string[] args, TestKind group, string? filter) =>
        args.Contains("--all", StringComparer.Ordinal) || filter is not null || group is TestKind.Load;

    private static string DescribeEmptySelection(IReadOnlyList<TestCase> cases, TestKind kind, string? filter, string? exclude)
    {
        if (filter is null)
        {
            if (exclude is null)
            {
                return kind is TestKind.Regular
                    ? "no test cases registered"
                    : $"no {Name(kind)} test cases registered";
            }

            // Every case in the group was excluded, which is a statement about the substring rather than an
            // empty suite -- and "no test cases registered" sends the reader looking for a registration that
            // is there.
            return $"'--exclude {exclude}' leaves no {Name(kind)} test case to run";
        }

        // A filter that names only a group this run did not ask for is the likeliest reason for an empty
        // selection, and "no test case matches" sends the reader hunting for a typo in the name instead of
        // for the argument that would have run them. The exclusion is not applied to that count: those cases
        // belong to a group this run did not select, and the advice below is about running that group whole.
        foreach (var group in Groups)
        {
            if (group == kind)
            {
                continue;
            }

            var deferred = Select(cases, group, filter, exclude: null).Length;

            if (deferred == 0)
            {
                continue;
            }

            return group is TestKind.Regular
                ? $"'{filter}' matches {deferred} regular test(s), which only the default run selects"
                : $"'{filter}' matches {deferred} {Name(group)} test(s); pass {Flag(group)} to run them";
        }

        return exclude is null
            ? $"no test case matches '{filter}'"
            : $"no test case matches '{filter}' outside '{exclude}'";
    }

    /// <summary>
    /// Whether the command line names the removed <c>--backend</c> flag, which chose which Recycle Bin
    /// path a run measured. Refused rather than ignored, the way <c>--scratch</c> refuses a root nobody
    /// has: a flag that used to decide which code a whole run exercised must never read as accepted.
    /// </summary>
    /// <param name="args">The command line as it was given.</param>
    internal static bool IsRemovedFlagPresent(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return args.Contains("--backend", StringComparer.Ordinal);
    }

    /// <summary>The word a group's cases are counted in.</summary>

    private static string Name(TestKind kind) => kind switch
    {
        TestKind.Regular => "regular",
        _ => "load",
    };

    /// <summary>How to ask for a group this run did not select.</summary>
    private static string Flag(TestKind kind) => kind switch
    {
        TestKind.Regular => "no flag",
        _ => "--load",
    };

    /// <summary>What including a deferred group costs the caller, in the same sentence that names it.</summary>
    private static string Advice(TestKind kind) => kind switch
    {
        TestKind.Regular => "nothing selects them by name",
        _ => "pass --load to include them",
    };

    /// <summary>
    /// Reads the <c>--scratch</c> root, the volume every case of this run works on.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the flag was left out, given no path, or given a path that is not a
    /// volume root. Returning that rather than a fallback is the whole point: a default would put the suite's
    /// writes and its recursive deletes on a volume the caller never chose.
    /// </returns>
    private static bool TryParseScratchRoot(string[] args, out string scratchRoot, out string problem)
    {
        scratchRoot = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not "--scratch")
            {
                continue;
            }

            var value = i + 1 < args.Length ? args[i + 1] : null;

            // A flag is not a path, and on an argument whose value decides where files are written and
            // deleted, taking the next token either way would read a command line nobody wrote as the one
            // they did.
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            {
                problem = value is null
                    ? "--scratch is the last thing on the command line, so it has no volume root to work on."
                    : $"--scratch is followed by \"{value}\", which is a flag rather than a volume root.";

                return false;
            }

            try
            {
                scratchRoot = VolumeRoot.Require(value, "--scratch");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Reported rather than thrown: a root nobody has is a command line to correct, and a stack
                // trace from inside the runner says nothing about which argument was wrong.
                problem = ex.Message;
                return false;
            }

            i++;
            problem = string.Empty;

            return true;
        }

        problem = "The command line does not name a volume to test on.";
        return false;
    }

    /// <summary>
    /// Reads the <c>--filter</c> substring, and refuses the two command lines that name no substring.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the flag has nothing usable after it. Returning a null filter instead
    /// would run the whole regular group: a narrowing flag that quietly does not narrow is a run nobody
    /// asked for, and reading a report off it afterwards says nothing about what was tested.
    /// </returns>
    private static bool TryParseFilter(string[] args, out string? filter, out string problem) =>
        TryParseSubstring(args, ["--filter", "-f"], out filter, out problem);

    /// <summary>
    /// Reads the <c>--exclude</c> substring, refusing exactly the command lines <c>--filter</c> refuses.
    /// </summary>
    private static bool TryParseExclude(string[] args, out string? exclude, out string problem) =>
        TryParseSubstring(args, ["--exclude", "-x"], out exclude, out problem);

    /// <summary>
    /// Reads the substring a name-matching flag was given, under the one rule both of them follow.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the flag has nothing usable after it. Returning null instead leaves the
    /// flag a no-op, and a flag that quietly keeps every case is the same run nobody asked for as one that
    /// quietly runs them all.
    /// </returns>
    private static bool TryParseSubstring(string[] args, string[] flags, out string? substring, out string problem)
    {
        substring = null;
        problem = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (!flags.Contains(args[i], StringComparer.Ordinal))
            {
                continue;
            }

            var name = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : null;

            // The same rule --scratch follows: a flag is not a substring. Taking the next token
            // either way turns `--filter --load` into a filter matching "--load", which selects nothing and
            // reports an empty run rather than the typo it is.
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            {
                problem = value is null
                    ? $"{name} is the last thing on the command line, so it has no substring to match."
                    : $"{name} is followed by \"{value}\", which is a flag rather than a substring to match.";

                return false;
            }

            substring = value;
            return true;
        }

        return true;
    }
}
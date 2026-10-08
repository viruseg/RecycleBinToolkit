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
    private static readonly TestKind[] Groups = [TestKind.Regular, TestKind.Load, TestKind.Manual];

    /// <summary>
    /// Runs the cases selected by the command line.
    /// </summary>
    /// <param name="args">
    /// <c>--scratch &lt;root&gt;</c> names the volume every case works on and is required. <c>--load</c>
    /// selects the load group; <c>--manual &lt;root&gt;</c> selects the manual group and may
    /// be repeated, in which case every selected manual case runs once per root, with that root handed to
    /// the case. Without either, the regular group is selected. <c>--all</c> and no arguments select every
    /// regular case, <c>--filter &lt;substring&gt;</c> narrows whichever group was selected to the cases
    /// whose name contains the substring. The groups never overlap: no argument other than <c>--load</c>
    /// runs a load case, and no argument other than <c>--manual</c> runs a manual one.
    /// </param>
    /// <param name="cases">All known cases; grouping and filtering both happen inside the runner.</param>
    /// <returns>
    /// 0 when every selected case passed, 1 when one failed or the selection was empty, 2 when
    /// <c>--scratch</c> was left out or was given no root, when <c>--manual</c> was given without a
    /// root for it to name, or when <c>--filter</c> was given nothing to match.
    /// </returns>
    internal static async Task<int> RunAsync(string[] args, IReadOnlyList<TestCase> cases)
    {
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

        if (!TryParseManualRoots(args, out var manualRoots, out var manualProblem))
        {
            Console.Error.WriteLine(
                "--manual takes a path to test, such as --manual \"F:\\\" or --manual \"\\\\server\\share\\\". The manual " +
                "cases destroy files on the path you name, so none of them runs until you give one, and a flag " +
                "after --manual is read as a missing path rather than as a root. A command line that cannot " +
                "carry two leading backslashes may name the share as //server/share/ instead, which is read " +
                "as the same volume.");
            Console.Error.WriteLine($"        {manualProblem}");
            return 2;
        }

        if (!TryParseFilter(args, out var filter, out var filterProblem))
        {
            Console.Error.WriteLine(
                "--filter takes the substring a test name must contain, such as --filter LongPathSuite or -f Empty. " +
                "A flag after it is read as a missing substring rather than as one, and no substring at all means " +
                "no narrowing rather than an error -- so the run you asked for is the run that happens.");
            Console.Error.WriteLine($"        {filterProblem}");
            return 2;
        }

        var kind = SelectKind(manualRoots, args);

        ScratchWorkspace.Configure(scratchRoot);

        var selected = Select(cases, kind, filter);

        if (selected.Length == 0)
        {
            Console.Error.WriteLine(DescribeEmptySelection(cases, kind, filter));
            return 1;
        }

        var runs = Expand(selected, manualRoots);

        Console.WriteLine($"scratch root {ScratchWorkspace.RunRoot}");
        Console.WriteLine($"running {runs.Count} of {cases.Count} test(s)");

        if (kind is TestKind.Manual && args.Contains("--load", StringComparer.Ordinal))
        {
            // One run selects one group, and a root was named. Saying which flag lost is the whole point:
            // the deferral lines below are there so a caller always learns what a run left out, and a
            // dropped --load is exactly the case where nothing else would say so.
            Console.WriteLine("        --load is ignored: --manual named a path to test, and this run selects the manual group");
        }

        DescribeDeferred(cases, kind, filter);

        Console.WriteLine();

        // Everything below writes files and removes directories, so the run's own tree goes with it rather
        // than being left in the volume root for the next run to trip over. A run that fails still cleans
        // up: the cases report through `runs`, not through what is left on disk.
        try
        {
            return await ExecuteAsync(runs).ConfigureAwait(false);
        }
        finally
        {
            ScratchWorkspace.ReleaseRunRoot();
        }
    }

    /// <summary>
    /// Runs the selected cases in order and reports what they did.
    /// </summary>
    /// <param name="runs">One entry per case, or per case per named root for the manual group.</param>
    /// <returns>0 when every case passed, 1 when one failed.</returns>
    private static async Task<int> ExecuteAsync(List<(TestCase Case, string? Root)> runs)
    {
        var passed = 0;
        var skipped = 0;
        List<string>? failures = null;

        foreach (var (testCase, root) in runs)
        {
            var name = root is null ? testCase.Name : $"{testCase.Name} [{root}]";
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(TestBudget);
                await testCase.Body(new TestContext(cts.Token, root)).ConfigureAwait(false);
                stopwatch.Stop();
                passed++;
                Console.WriteLine($"  PASS  {name} ({stopwatch.ElapsedMilliseconds} ms)");
            }
            catch (CheckSkippedException skip)
            {
                // Counted apart from both outcomes. A case that cannot run on this machine produced no
                // coverage, and a run that calls it a pass says otherwise -- which is the failure the
                // deferral lines exist to prevent, applied to a case rather than to a group.
                stopwatch.Stop();
                skipped++;
                Console.WriteLine($"  SKIP  {name}");
                Console.WriteLine($"        {skip.Message}");
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                (failures ??= []).Add(name);
                Console.WriteLine($"  FAIL  {name} ({stopwatch.ElapsedMilliseconds} ms)");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");

                if (ex is not CheckFailedException)
                {
                    Console.WriteLine($"        {ex.StackTrace?.Replace(Environment.NewLine, Environment.NewLine + "        ")}");
                }
            }
        }

        Console.WriteLine();
        // Counted from the runs rather than the selection: a manual run is one case per named root, and a
        // tally that read the selection would report fewer failures than it printed.
        var failedCount = runs.Count - passed - skipped;
        Console.WriteLine(Summary(passed, skipped, failedCount));

        if (failures is not null)
        {
            Console.WriteLine("failed: " + string.Join(", ", failures));
        }

        return failedCount == 0 ? 0 : 1;
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

    /// <summary>
    /// The runs a selection stands for: one per case, or one per case per named root for the manual
    /// group, which is the only group whose answer depends on what the caller named.
    /// </summary>
    private static List<(TestCase Case, string? Root)> Expand(
        IReadOnlyList<TestCase> selected,
        IReadOnlyList<string> manualRoots)
    {
        var runs = new List<(TestCase Case, string? Root)>(selected.Count);

        foreach (var testCase in selected)
        {
            if (manualRoots.Count == 0)
            {
                runs.Add((testCase, null));
                continue;
            }

            runs.AddRange(manualRoots.Select(root => (testCase, Root: (string?)root)));
        }

        return runs;
    }

    /// <summary>
    /// Which group the command line asks for. A named root outranks <c>--load</c>: the caller named a
    /// path to be tested, and a load group says nothing about which.
    /// </summary>
    /// <remarks>
    /// The run says so on stdout rather than refusing the pair. One run selects one group whichever way
    /// this goes, and telling a caller which group they got keeps a single command working — while
    /// refusing would leave a command that names a real path silently selecting nothing useful instead.
    /// </remarks>
    private static TestKind SelectKind(IReadOnlyList<string> manualRoots, string[] args) =>
        manualRoots.Count > 0
            ? TestKind.Manual
            : args.Contains("--load", StringComparer.Ordinal) ? TestKind.Load : TestKind.Regular;

    /// <summary>
    /// Names the groups this run left out, one line each.
    /// </summary>
    /// <remarks>
    /// Left out silently, a case reads as "everything passed" — which is exactly the wrong conclusion to
    /// draw about a case nobody asked for, and it is the only way to tell afterwards that a run never
    /// reached it. Only the regular run reports a deferral: the other two are asked for by name, so what
    /// they leave out is the caller's own choice rather than a group nobody was offered.
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

            var deferred = Select(cases, group, filter).Length;
            if (deferred > 0)
            {
                Console.WriteLine($"        {deferred} {Name(group)} test(s) match and are not part of this run; {Advice(group)}");
            }
        }
    }

    private static TestCase[] Select(IReadOnlyList<TestCase> cases, TestKind kind, string? filter) =>
        [.. cases.Where(c => c.Kind == kind && (filter is null || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))];

    private static string DescribeEmptySelection(IReadOnlyList<TestCase> cases, TestKind kind, string? filter)
    {
        if (filter is null)
        {
            return kind is TestKind.Regular
                ? "no test cases registered"
                : $"no {Name(kind)} test cases registered";
        }

        // A filter that names only a group this run did not ask for is the likeliest reason for an empty
        // selection, and "no test case matches" sends the reader hunting for a typo in the name instead of
        // for the argument that would have run them.
        foreach (var group in Groups)
        {
            if (group == kind)
            {
                continue;
            }

            var deferred = Select(cases, group, filter).Length;

            if (deferred == 0)
            {
                continue;
            }

            return group is TestKind.Regular
                ? $"'{filter}' matches {deferred} regular test(s), which only the default run selects"
                : $"'{filter}' matches {deferred} {Name(group)} test(s); pass {Flag(group)} to run them";
        }

        return $"no test case matches '{filter}'";
    }

    /// <summary>The word a group's cases are counted in.</summary>
    private static string Name(TestKind kind) => kind switch
    {
        TestKind.Regular => "regular",
        TestKind.Load => "load",
        _ => "manual",
    };

    /// <summary>How to ask for a group this run did not select.</summary>
    private static string Flag(TestKind kind) => kind switch
    {
        TestKind.Regular => "no flag",
        TestKind.Load => "--load",
        _ => "--manual <root>",
    };

    /// <summary>What including a deferred group costs the caller, in the same sentence that names it.</summary>
    private static string Advice(TestKind kind) => kind switch
    {
        TestKind.Regular => "nothing selects them by name",
        TestKind.Load => "pass --load to include them",
        _ => "pass --manual <root> to include them; the paths are the caller's, and the cases destroy files on them",
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

            // The same rule --manual follows, for the same reason: a flag is not a path, and on an argument
            // whose value decides where files are written and deleted, taking the next token either way would
            // read a command line nobody wrote as the one they did.
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
    /// Reads the <c>--manual</c> roots, in the order they were given.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the flag has no path after it, or an empty one. Returning that rather
    /// than an empty list is what keeps <c>--manual</c> on its own from running the regular group and
    /// reading afterwards as though the manual cases had been asked for.
    /// </returns>
    private static bool TryParseManualRoots(string[] args, out List<string> roots, out string problem)
    {
        roots = [];
        problem = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not "--manual")
            {
                continue;
            }

            var value = i + 1 < args.Length ? args[i + 1] : null;

            // A flag is not a path. Taking the next token either way turns `--manual --load` into a root
            // named "--load", and a command line with --manual copied ahead of --load loses the flag after
            // it without a word — on a flag whose cases delete files, the argument it reads is the one thing
            // that must not be guessed.
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            {
                problem = value is null
                    ? "--manual is the last thing on the command line, so it has no path to test."
                    : $"--manual is followed by \"{value}\", which is a flag rather than a path to test.";

                return false;
            }

            try
            {
                roots.Add(VolumeRoot.Normalize(value));
            }
            catch (ArgumentException ex)
            {
                problem = ex.Message;
                return false;
            }

            i++;
        }

        return true;
    }

    /// <summary>
    /// Reads the <c>--filter</c> substring, and refuses the two command lines that name no substring.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the flag has nothing usable after it. Returning a null filter instead
    /// would run the whole regular group: a narrowing flag that quietly does not narrow is a run nobody
    /// asked for, and reading a report off it afterwards says nothing about what was tested.
    /// </returns>
    private static bool TryParseFilter(string[] args, out string? filter, out string problem)
    {
        filter = null;
        problem = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is not ("--filter" or "-f"))
            {
                continue;
            }

            var value = i + 1 < args.Length ? args[i + 1] : null;

            // The same rule --scratch and --manual follow: a flag is not a substring. Taking the next token
            // either way turns `--filter --load` into a filter matching "--load", which selects nothing and
            // reports an empty run rather than the typo it is.
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            {
                problem = value is null
                    ? "--filter is the last thing on the command line, so it has no substring to match."
                    : $"--filter is followed by \"{value}\", which is a flag rather than a substring to match.";

                return false;
            }

            filter = value;
            return true;
        }

        return true;
    }
}
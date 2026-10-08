namespace RecycleBinToolkit.Tests.Host;

/// <summary>
/// Which group a test belongs to. The group decides whether an ordinary run picks the test up.
/// </summary>
internal enum TestKind
{
    /// <summary>An ordinary case. Runs whenever no group is named on the command line.</summary>
    Regular,

    /// <summary>
    /// A case that puts real load on the CPU or the disk — volume, repetition, or a bound measured in
    /// elapsed time. It runs only when <c>--load</c> is passed, because it is slow enough to matter and
    /// its failures are about the machine's state rather than about the code.
    /// </summary>
    Load,

    /// <summary>
    /// A case that needs hardware this repository cannot arrange — a removable volume or a network share
    /// with no Recycle Bin — and <b>destroys files on the path the caller names</b>.
    /// </summary>
    /// <remarks>
    /// It runs only when <c>--manual &lt;root&gt;</c> is passed, once per root given, and is never
    /// selected by <c>--all</c>, <c>--filter</c> or <c>--load</c>: a default run must not depend on a
    /// flash drive being plugged in or on somebody's share being reachable. A run that names a root and
    /// passes <c>--load</c> selects this group and says that it ignored the other flag, because one run
    /// selects one group and a flag that vanishes quietly is the failure the deferral lines exist to
    /// prevent.
    /// <para>
    /// Nothing hard-codes a root. The caller owns the path, and a case that treats it as a scratch
    /// directory is why this group is behind a flag rather than in the default run.
    /// </para>
    /// </remarks>
    Manual,
}

/// <summary>
/// A single named test.
/// </summary>
/// <param name="Name">Unique name, also usable as the <c>--filter</c> argument.</param>
/// <param name="Body">Test body. Receives a context carrying the per-test cancellation token.</param>
/// <param name="Kind">
/// Group the case belongs to. Defaults to <see cref="TestKind.Regular"/>, so only a case that is
/// deliberately a load case names it.
/// </param>
internal sealed record TestCase(string Name, Func<TestContext, Task> Body, TestKind Kind = TestKind.Regular);

/// <summary>
/// Per-test execution context.
/// </summary>
internal sealed class TestContext
{
    internal TestContext(CancellationToken cancellationToken, string? manualRoot = null)
    {
        CancellationToken = cancellationToken;
        ManualRoot = manualRoot;
    }

    /// <summary>
    /// Cancelled when the test exceeds its time budget or when the runner is asked to stop.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// The path <c>--manual</c> was given for this run, in the spelling the library reads a volume root in,
    /// or <see langword="null"/> in every other group.
    /// </summary>
    /// <remarks>
    /// A case may destroy files under it. It is the caller's volume or share, not a scratch directory, so
    /// anything a manual case creates belongs in a subdirectory of its own and is removed by full path.
    /// </remarks>
    public string? ManualRoot { get; }
}
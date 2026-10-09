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
    internal TestContext(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Cancelled when the test exceeds its time budget or when the runner is asked to stop.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
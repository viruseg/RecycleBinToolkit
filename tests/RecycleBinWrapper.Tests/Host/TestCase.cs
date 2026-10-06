namespace RecycleBinWrapper.Tests.Host;

/// <summary>
/// A single named test.
/// </summary>
/// <param name="Name">Unique name, also usable as the <c>--filter</c> argument.</param>
/// <param name="Body">Test body. Receives a context carrying the per-test cancellation token.</param>
internal sealed record TestCase(string Name, Func<TestContext, Task> Body);

/// <summary>
/// Per-test execution context.
/// </summary>
internal sealed class TestContext
{
    internal TestContext(CancellationToken cancellationToken) => CancellationToken = cancellationToken;

    /// <summary>
    /// Cancelled when the test exceeds its time budget or when the runner is asked to stop.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
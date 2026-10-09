using RecycleBinToolkit;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Which Recycle Bin path this run measures.
/// </summary>
internal enum TestBackendKind
{
    /// <summary>The Shell path: STA worker, <c>IFileOperation</c>, the Shell's virtual folder.</summary>
    WinApi,

    /// <summary>The native path: <c>$RECYCLE.BIN</c> records and payloads, read and written directly.</summary>
    Native,
}

/// <summary>
/// The backend a run was told to measure, and the options that carry it into the library.
/// </summary>
/// <remarks>
/// A run measures one backend, chosen on the command line, because the two are not the same code and a run
/// that quietly mixed them would report coverage it does not have. Which one is printed in the run's header
/// so that a report always says what it measured.
/// <para>
/// The suites that test the Shell implementation's internals do not read this. The flag is a caller's
/// choice, not a statement about which code exists: <c>WorkerSuite</c> tests the STA worker whether or not
/// anybody recycles anything, and that work is the same either way.
/// </para>
/// </remarks>
internal static class TestBackend
{
    private const string Flag = "--backend";

    private static TestBackendKind? _kind;

    private static RecycleBinOptions? _options;

    /// <summary>The backend this run measures.</summary>
    public static TestBackendKind Kind => _kind ??= TestBackendKind.WinApi;

    /// <summary>Whether this run measures the native path.</summary>
    public static bool IsNative => Kind == TestBackendKind.Native;

    /// <summary>
    /// The options every case in a facade suite passes to the library, carrying this run's backend.
    /// </summary>
    public static RecycleBinOptions Options => _options ??= new RecycleBinOptions { Backend = (RecycleBinBackend)Kind };

    /// <summary>
    /// Reads the backend off a command line, refusing anything that is not one.
    /// </summary>
    /// <param name="args">The command line as it was given.</param>
    /// <param name="problem">
    /// Receives why the value was refused, or an empty string when it was accepted or absent.
    /// </param>
    /// <returns>
    /// The backend named, or <see cref="TestBackendKind.WinApi"/> when the flag is absent.
    /// </returns>
    /// <remarks>
    /// Reported rather than thrown, the way the volume is: a command line to correct is a thing to print
    /// with the argument that is wrong, not a stack trace from inside the runner.
    /// </remarks>
    public static TestBackendKind Read(string[] args, out string problem)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != Flag)
            {
                continue;
            }

            var value = i + 1 < args.Length ? args[i + 1] : null;

            // A flag is not a backend name, and on an argument that decides which code a whole run exercises,
            // taking the next token either way would read a command line nobody wrote as the one they did.
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-'))
            {
                problem = value is null
                    ? "--backend is the last thing on the command line, so it names no backend to measure."
                    : $"--backend is followed by \"{value}\", which is a flag rather than a backend name.";

                return TestBackendKind.WinApi;
            }

            problem = value.ToLowerInvariant() switch
            {
                "winapi" => string.Empty,
                "native" => string.Empty,
                _ => $"--backend was given \"{value}\", which is not a backend this library has. Use winapi or native."
            };

            return problem.Length == 0 ? Parse(value) : TestBackendKind.WinApi;
        }

        problem = string.Empty;
        return TestBackendKind.WinApi;
    }

    /// <summary>
    /// Fixes the backend for this run. Called by the runner before a single case runs.
    /// </summary>
    /// <param name="kind">What the command line named.</param>
    public static void Configure(TestBackendKind kind)
    {
        _kind = kind;
        _options = null;
    }

    private static TestBackendKind Parse(string value) =>
        value.Equals("native", StringComparison.OrdinalIgnoreCase)
            ? TestBackendKind.Native
            : TestBackendKind.WinApi;
}
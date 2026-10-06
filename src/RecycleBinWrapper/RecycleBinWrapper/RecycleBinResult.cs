namespace Utils;

/// <summary>
/// Point in the recycle pipeline at which an item failed.
/// </summary>
public enum RecycleFailureStage
{
    /// <summary>The path was empty, malformed, or pointed at nothing.</summary>
    PathValidation,

    /// <summary>The Shell refused to produce an item for the path.</summary>
    ShellItemCreation,

    /// <summary>The item could not be added to the pending operation.</summary>
    Queueing,

    /// <summary>The Shell performed the delete and reported a failure for this item.</summary>
    Performing,

    /// <summary>The delete reported success but the result did not reach the Recycle Bin.</summary>
    ReceiptVerification
}

/// <summary>
/// One item that did not reach the Recycle Bin.
/// </summary>
public sealed class RecycleFailure
{
    /// <summary>
    /// Creates a failure record.
    /// </summary>
    /// <param name="path">The path exactly as the caller supplied it.</param>
    /// <param name="hResult">Shell error code, or 0 when the Shell was never reached.</param>
    /// <param name="attempts">How many attempts were made before giving up.</param>
    /// <param name="stage">Where in the pipeline the failure occurred.</param>
    public RecycleFailure(string path, int hResult, int attempts, RecycleFailureStage stage)
    {
        Path = path;
        HResult = hResult;
        Attempts = attempts;
        Stage = stage;
    }

    /// <summary>The path exactly as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>
    /// Shell error code, or 0 when the Shell was never reached. A delete the Shell refused
    /// reports the severity bit set; check <c>(HResult &amp; 0x80000000) != 0</c> to tell a
    /// failure from a success code such as <c>0x00270008</c>.
    /// </summary>
    public int HResult { get; }

    /// <summary>How many attempts were made before giving up.</summary>
    public int Attempts { get; }

    /// <summary>Where in the pipeline the failure occurred.</summary>
    public RecycleFailureStage Stage { get; }
}

/// <summary>
/// Outcome of a Recycle Bin operation.
/// </summary>
public sealed class RecycleBinResult
{
    private RecycleBinResult(IReadOnlyList<string> recycledPaths, IReadOnlyList<RecycleFailure> failures, TimeSpan elapsed)
    {
        RecycledPaths = recycledPaths;
        Failures = failures;
        Elapsed = elapsed;
    }

    /// <summary>
    /// <see langword="true"/> when every requested path reached the Recycle Bin.
    /// </summary>
    public bool Succeeded => Failures.Count == 0;

    /// <summary>Paths, as supplied by the caller, that were confirmed recycled.</summary>
    public IReadOnlyList<string> RecycledPaths { get; }

    /// <summary>
    /// Paths that did not reach the Recycle Bin. Empty when <see cref="Succeeded"/> is
    /// <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<RecycleFailure> Failures { get; }

    /// <summary>Wall-clock time spent on the operation, including retries.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>
    /// The first recorded failure, or <see langword="null"/> when the operation succeeded.
    /// </summary>
    public RecycleFailure? FirstFailure => Failures.Count > 0 ? Failures[0] : null;

    /// <summary>
    /// Creates a result.
    /// </summary>
    /// <param name="recycledPaths">Paths confirmed recycled, in caller-supplied form.</param>
    /// <param name="failures">Paths that did not reach the Recycle Bin.</param>
    /// <param name="elapsed">Wall-clock time spent on the operation.</param>
    public static RecycleBinResult Create(
        IReadOnlyList<string> recycledPaths,
        IReadOnlyList<RecycleFailure> failures,
        TimeSpan elapsed) => new(recycledPaths, failures, elapsed);
}
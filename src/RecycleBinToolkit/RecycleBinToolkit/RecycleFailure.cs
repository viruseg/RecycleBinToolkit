namespace RecycleBinToolkit;

/// <summary>One item a call did not handle.</summary>
/// <typeparamparam name="TStage">
/// Why it was not handled, in the terms of the operation that reported it. Each operation carries one such
/// enumeration, so a stage that operation cannot produce will not compile.
/// </typeparamparam>
public sealed class RecycleFailure<TStage>
    where TStage : struct, Enum
{
    /// <summary>Creates a failure record.</summary>
    /// <param name="path">The path the failure is about, in full.</param>
    /// <param name="hResult">
    /// Error code, or <c>0</c> when nothing was attempted. Carried through unchanged.
    /// </param>
    /// <param name="attempts">How many attempts were made.</param>
    /// <param name="stage">Where the failure occurred.</param>
    public RecycleFailure(string path, int hResult, int attempts, TStage stage)
    {
        Path = path;
        HResult = hResult;
        Attempts = attempts;
        Stage = stage;
    }

    /// <summary>The path the failure is about, in full.</summary>
    /// <remarks>
    /// What that path names is a property of <see cref="Stage"/>, not of this class: for a
    /// <see cref="RecyclePathFailureStage"/> it is the path the caller supplied, and for the other three it
    /// is something inside the Recycle Bin or a volume root the caller did not name.
    /// </remarks>
    public string Path { get; }

    /// <summary>The file system's own code for this item, or <c>0</c> when nothing was attempted.</summary>
    /// <remarks>
    /// Check <c>(HResult &amp; 0x80000000) != 0</c> to tell a refusal from a success code.
    /// </remarks>
    public int HResult { get; }

    /// <summary>
    /// Attempts made for this item. <c>0</c> means nothing was attempted, and
    /// <see cref="HResult"/> carries no code either.
    /// </summary>
    public int Attempts { get; }

    /// <summary>Where the failure occurred, in the terms of the operation that reported it.</summary>
    public TStage Stage { get; }
}
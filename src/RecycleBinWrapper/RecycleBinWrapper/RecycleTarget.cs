namespace Utils;

/// <summary>
/// One path to recycle, in both the form the caller supplied and the normalised form the Shell
/// needs.
/// </summary>
internal sealed class RecycleTarget
{
    /// <summary>
    /// Creates a target.
    /// </summary>
    /// <param name="inputPath">The caller's path, kept verbatim so diagnostics point at what they wrote.</param>
    /// <param name="fullPath">The normalised absolute path handed to the Shell.</param>
    internal RecycleTarget(string inputPath, string fullPath)
    {
        InputPath = inputPath;
        FullPath = fullPath;
    }

    /// <summary>The caller's path, kept verbatim so diagnostics point at what they wrote.</summary>
    public string InputPath { get; }

    /// <summary>The normalised absolute path handed to the Shell.</summary>
    public string FullPath { get; }
}
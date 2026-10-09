using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The three Recycle Bin operations, as one implementation or the other.
/// </summary>
/// <remarks>
/// The seam exists for one property rather than for tidiness: <see cref="ShellRecycleBinBackend"/>
/// is the only implementation allowed to reach <see cref="RecycleBinWorker"/>, so no code behind the
/// native backend can reach the single-threaded apartment — not by accident, not by a refactor that
/// adds an <c>if</c> in the wrong place. Branching inside the Shell operations instead would put that
/// <c>if</c> at every point where the Shell is reached, and each of those is a place to get it wrong.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal interface IRecycleBinBackend
{
    /// <summary>
    /// Moves a batch of validated paths to the Recycle Bin.
    /// </summary>
    /// <param name="targets">Paths already validated and cleared of volumes that cannot recycle them.</param>
    /// <param name="options">Retry and verification settings, already normalised.</param>
    /// <param name="cancellationToken">Cancels the wait, and stops the work between paths where the implementation can.</param>
    /// <returns>The outcome of the batch. Never throws for a per-item failure.</returns>
    Task<RecycleBinResult> RecycleAsync(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken);
}
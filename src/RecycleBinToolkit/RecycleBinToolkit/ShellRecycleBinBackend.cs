using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// The Recycle Bin operations as this library has always done them: the Shell, on the STA worker.
/// </summary>
/// <remarks>
/// This is where the Shell path lives so that it is the only thing that reaches
/// <see cref="RecycleBinWorker"/>. The body is the one <see cref="FileRecycler"/> used to call, moved
/// without change — the worker, the retry rounds, the per-item diagnostics and every comment explaining
/// why they are the way they are.
/// <para>
/// Reading and emptying reach the Shell here too, and arrive with the operations that implement them
/// on the native backend. Until then they stay on <see cref="RecycleBin"/>, where they have always
/// been, rather than moving twice.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class ShellRecycleBinBackend : IRecycleBinBackend
{
    private static readonly ShellRecycleBinBackend Shared = new();

    private ShellRecycleBinBackend()
    {
    }

    /// <summary>The one instance. The backend holds no state, so there is nothing to have more than one of.</summary>
    internal static ShellRecycleBinBackend Instance => Shared;

    /// <summary>
    /// Hands a batch to the Shell worker and completes when the Shell has finished with it.
    /// </summary>
    /// <param name="targets">Paths to move to the Recycle Bin.</param>
    /// <param name="options">Retry and verification settings.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait. Cancellation never tears the worker down: a request already handed to the
    /// Shell runs to completion, so a cancelled caller cannot leave a half-performed operation.
    /// </param>
    /// <returns>The outcome of the batch.</returns>
    public Task<RecycleBinResult> RecycleAsync(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken) =>
        RecycleBinWorker.SubmitAsync(targets, options.Normalize(), cancellationToken);
}
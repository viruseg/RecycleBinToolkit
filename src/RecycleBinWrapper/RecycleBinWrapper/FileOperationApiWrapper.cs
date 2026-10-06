using System.Runtime.Versioning;

namespace Utils;

/// <summary>
/// Moves files and directories to the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// Safe to call from any thread, including one that is already on a single-threaded apartment.
/// Shell work runs on a dedicated worker thread, so callers never pay for COM initialisation and
/// never block inside the Shell.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static class FileOperationApiWrapper
{
    /// <summary>
    /// Moves a single file or directory to the Recycle Bin.
    /// </summary>
    /// <param name="filePath">Path of the file or directory to recycle.</param>
    /// <param name="throwOnError">
    /// When <see langword="true"/> (the default), a path that cannot be recycled raises an
    /// exception carrying the reason. When <see langword="false"/>, the call returns
    /// <see langword="false"/> instead.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait for the Shell. Cancelling after the work started does not stop the Shell, so
    /// the item may still end up in the Recycle Bin.
    /// </param>
    /// <returns><see langword="true"/> when the path reached the Recycle Bin.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is null, empty or whitespace and <paramref name="throwOnError"/>
    /// is <see langword="true"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist and <paramref name="throwOnError"/> is
    /// <see langword="true"/>.
    /// </exception>
    /// <exception cref="IOException">
    /// The Shell refused the delete and <paramref name="throwOnError"/> is
    /// <see langword="true"/>. <see cref="Exception.HResult"/> carries the Shell error code.
    /// </exception>
    public static async Task<bool> MoveToRecycleBinAsync(
        string? filePath,
        bool throwOnError = true,
        CancellationToken cancellationToken = default)
    {
        if (throwOnError)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        }
        else if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var result = await MoveToRecycleBinAsync([filePath], null, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return true;
        }

        if (throwOnError)
        {
            ThrowForSinglePath(result.FirstFailure!);
        }

        return false;
    }

    /// <summary>
    /// Moves a batch of files or directories to the Recycle Bin.
    /// </summary>
    /// <param name="filePaths">
    /// Paths to recycle. Entries may be <see langword="null"/> or refer to paths that do not exist;
    /// each such entry is reported as a failure without aborting the rest of the batch. Repeated
    /// paths are recycled once.
    /// </param>
    /// <param name="options">
    /// Retry and verification settings. <see langword="null"/> means
    /// <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait between attempts. A round already handed to the Shell always finishes.
    /// </param>
    /// <returns>
    /// The outcome of the batch. Never throws for a per-item failure; read
    /// <see cref="RecycleBinResult.Failures"/> for the diagnostics.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<RecycleBinResult> MoveToRecycleBinAsync(
        IReadOnlyList<string?> filePaths,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var (targets, validationFailures) = BuildTargets(filePaths);

        if (targets.Count == 0)
        {
            return RecycleBinResult.Create([], validationFailures, TimeSpan.Zero);
        }

        var result = await RecycleBinWorker
            .SubmitAsync(targets, options ?? RecycleBinOptions.Default, cancellationToken)
            .ConfigureAwait(false);

        if (validationFailures.Count == 0)
        {
            return result;
        }

        return RecycleBinResult.Create(
            result.RecycledPaths,
            [.. validationFailures, .. result.Failures],
            result.Elapsed);
    }

    private static void ThrowForSinglePath(RecycleFailure failure)
    {
        if (failure.Stage == RecycleFailureStage.PathValidation && !string.IsNullOrEmpty(failure.Path))
        {
            RecycleBinThrowHelper.ThrowFileNotFound(failure.Path);
        }

        RecycleBinThrowHelper.ThrowRecycleFailed(failure);
    }

    private static (List<RecycleTarget> Targets, List<RecycleFailure> ValidationFailures) BuildTargets(
        IReadOnlyList<string?> filePaths)
    {
        var targets = new List<RecycleTarget>(filePaths.Count);
        var failures = new List<RecycleFailure>();

        for (var i = 0; i < filePaths.Count; i++)
        {
            var failure = TryCreateTarget(filePaths[i], out var target);
            if (failure is null)
            {
                targets.Add(target!);
            }
            else
            {
                failures.Add(failure);
            }
        }

        return (targets, failures);
    }

    private static RecycleFailure? TryCreateTarget(string? rawPath, out RecycleTarget? target)
    {
        target = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new RecycleFailure(rawPath ?? string.Empty, 0, 0, RecycleFailureStage.PathValidation);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return new RecycleFailure(rawPath, ex.HResult, 0, RecycleFailureStage.PathValidation);
        }

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return new RecycleFailure(rawPath, 0, 0, RecycleFailureStage.PathValidation);
        }

        target = new RecycleTarget(rawPath, fullPath);
        return null;
    }
}
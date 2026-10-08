using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves files and directories to the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// Safe to call from any thread, including one that is already on a single-threaded apartment.
/// Shell work runs on a dedicated worker thread, so callers never pay for COM initialisation and
/// never block inside the Shell.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static class FileRecycler
{
    /// <summary>
    /// Moves a single file or directory to the Recycle Bin.
    /// </summary>
    /// <param name="filePath">Path of the file or directory to recycle.</param>
    /// <param name="options">
    /// Retry, verification and error-handling settings. <see langword="null"/> means
    /// <see cref="RecycleBinOptions.Default"/>. <see cref="RecycleBinOptions.ThrowOnError"/> decides
    /// whether a failure raises or is returned as <see langword="false"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait for the Shell. Cancelling after the work started does not stop the Shell, so
    /// the item may still end up in the Recycle Bin.
    /// </param>
    /// <returns><see langword="true"/> when the path was removed from disk.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is null, empty or whitespace and
    /// <see cref="RecycleBinOptions.ThrowOnError"/> is on.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist and <see cref="RecycleBinOptions.ThrowOnError"/> is
    /// on.
    /// </exception>
    /// <exception cref="PathTooLongException">
    /// The path is too long for the Shell to recycle, no short name is available, and
    /// <see cref="RecycleBinOptions.LongPathFailureAction"/> is
    /// <see cref="LongPathFailureAction.Throw"/>. The file is left in place.
    /// </exception>
    /// <exception cref="IOException">
    /// The Shell refused the delete and <see cref="RecycleBinOptions.ThrowOnError"/> is on.
    /// <see cref="Exception.HResult"/> carries the Shell error code.
    /// </exception>
    public static async Task<bool> MoveToRecycleBinAsync(
        string? filePath,
        RecycleBinOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = options ?? RecycleBinOptions.Default;

        if (effective.ThrowOnError)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        }
        else if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var result = await MoveToRecycleBinAsync([filePath], effective, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return true;
        }

        if (effective.ThrowOnError)
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

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<RecycleBinResult>(cancellationToken).ConfigureAwait(false);
        }

        // Validating a batch is one filesystem probe per path and, on the volume measured here, about
        // 11 ms each — so 20,000 paths is a little over three and a half minutes. Run inline that time is
        // the caller's own, because an async method runs everything before its first await on the
        // thread that called it: a caller on a UI thread would be frozen for the whole scan, and a caller
        // holding a lock would hold it. Hopping to the pool first costs a scheduling round trip and buys
        // back the caller's thread, which is the only one that has something else to be doing.
        var (targets, validationFailures) = await Task
            .Run(() => BuildTargets(filePaths), cancellationToken)
            .ConfigureAwait(false);

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

        return result.WithFailures([.. validationFailures, .. result.Failures]);
    }

    private static void ThrowForSinglePath(RecycleFailure failure)
    {
        switch (failure.Stage)
        {
            case RecycleFailureStage.LongPathNotRecyclable:
                RecycleBinThrowHelper.ThrowPathTooLong(failure);
                break;

            case RecycleFailureStage.PathValidation when !string.IsNullOrEmpty(failure.Path):
                RecycleBinThrowHelper.ThrowFileNotFound(failure.Path);
                break;

            default:
                RecycleBinThrowHelper.ThrowRecycleFailed(failure);
                break;
        }
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
                targets.Add(target!.Value);
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

        if (!Exists(fullPath))
        {
            return new RecycleFailure(rawPath, 0, 0, RecycleFailureStage.PathValidation);
        }

        target = new RecycleTarget(rawPath, fullPath, RecycleTarget.StripExtendedPrefix(fullPath));
        return null;
    }

    /// <summary>
    /// Whether a path is there at all, file or directory.
    /// </summary>
    /// <remarks>
    /// One question rather than two. <c>File.Exists(a) || Directory.Exists(a)</c> costs two filesystem
    /// probes for every directory in the batch, because the file probe has to fail first, and it is
    /// asked once per path before the Shell is involved — 20,000 paths measured at 11 ms each, which is
    /// 220 seconds of the caller's thread before the first await.
    /// <para>
    /// Asking for the attributes instead answers both at once, since a directory reports
    /// <see cref="FileAttributes.Directory"/> and a file does not. It throws where the probes returned
    /// false, so the fallbacks are what keep a path the file system would not describe from being reported
    /// as absent.
    /// </para>
    /// </remarks>
    private static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return File.Exists(path) || Directory.Exists(path);
        }
    }
}
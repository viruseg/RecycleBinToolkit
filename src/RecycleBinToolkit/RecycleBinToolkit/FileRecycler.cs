using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Moves files and directories to the Windows Recycle Bin.
/// </summary>
/// <remarks>
/// Safe to call from any thread, including one that is already on a single-threaded apartment.
/// Shell work runs on a dedicated worker thread, so callers never pay for COM initialisation and
/// never block inside the Shell.
/// <para>
/// A path whose volume cannot recycle it is settled before the Shell is asked, so it is never
/// removed for good while the operation reports it as recycled. What happens to it instead is
/// <see cref="RecycleBinOptions.NoRecycleBinAction"/>: left where it is, handed over, or removed by
/// this library and named in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>.
/// </para>
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
    /// The Shell refused the delete, the Shell faulted, or a staging directory was left on the volume.
    /// Also the volume holding the path cannot recycle it and
    /// <see cref="RecycleBinOptions.NoRecycleBinAction"/> is <see cref="NoRecycleBinAction.Throw"/>, or
    /// that action is <see cref="NoRecycleBinAction.DeletePermanently"/> and the file system refused
    /// the removal — and <see cref="RecycleBinOptions.ThrowOnError"/> is on. The file is left in place
    /// in every one of those cases. <see cref="Exception.HResult"/> carries the Shell's or the file
    /// system's own code, or <c>0</c> where the volume was refused without asking the Shell.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The path names its volume by GUID, which the Shell will not parse and this library cannot measure,
    /// and <see cref="RecycleBinOptions.ThrowOnError"/> is on. Nothing was attempted and the file is
    /// where it was left. A path in that form has no other spelling, so address the same location by drive
    /// letter or mount point instead.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Either a staging directory was left on the volume, which <see cref="RecycleBinOptions.ThrowOnError"/>
    /// turns into a raise rather than <see cref="RecycleBinResult.LeftoverStagingDirectories"/>, or the
    /// Shell worker could not be brought up at all, which nothing caller-side can affect and which carries
    /// the underlying failure inside it.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
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
            ThrowForSinglePath(result);
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
    /// Retry, verification and error-handling settings. <see langword="null"/> means
    /// <see cref="RecycleBinOptions.Default"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the wait between attempts. A round already handed to the Shell always finishes.
    /// </param>
    /// <returns>
    /// The outcome of the batch. Never throws for a per-item failure; read
    /// <see cref="RecycleBinResult.Failures"/> for the diagnostics. A path refused because its volume
    /// cannot recycle it arrives there at stage <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/>,
    /// and the paths on every other volume in the batch are still recycled.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// </exception>
    /// <exception cref="IOException">
    /// The Shell refused the delete, the Shell faulted, or a staging directory was left on a volume while
    /// the batch was being retried. Nothing was deleted; the original failure is carried inside.
    /// Per-item problems are still data, not exceptions.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The Shell worker could not be brought up, which nothing caller-side can affect. The underlying
    /// failure is carried inside.
    /// </exception>
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

        var effective = options ?? RecycleBinOptions.Default;

        // Validating a batch is one filesystem probe per path and, on the volume measured here, about
        // 11 ms each — so 20,000 paths is a little over three and a half minutes. Run inline that time is
        // the caller's own, because an async method runs everything before its first await on the
        // thread that called it: a caller on a UI thread would be frozen for the whole scan, and a caller
        // holding a lock would hold it. Hopping to the pool first costs a scheduling round trip and buys
        // back the caller's thread, which is the only one that has something else to be doing.
        //
        // The volume gate belongs on this side of the hop for the same reason and one more: inspecting a
        // volume asks the Shell, and on a share that is a network round trip which blocks for seconds.
        // The Shell worker is one STA thread that every other caller in the process queues behind, so a
        // probe run there would turn one dead share into a stall for all of them.
        var (targets, gateFailures, permanentlyDeleted) = await Task
            .Run(() => BuildTargets(filePaths, effective), cancellationToken)
            .ConfigureAwait(false);

        if (targets.Count == 0)
        {
            return RecycleBinResult.Create([], permanentlyDeleted, gateFailures, TimeSpan.Zero);
        }

        var result = await RecycleBinWorker
            .SubmitAsync(targets, effective, cancellationToken)
            .ConfigureAwait(false);

        if (gateFailures.Count == 0 && permanentlyDeleted.Count == 0)
        {
            return result;
        }

        // The gate's own verdicts come first, in the order the paths were supplied, because a caller
        // reading the list wants the path that never reached the Shell before the ones that did.
        var merged = new List<RecycleFailure>(gateFailures.Count + result.Failures.Count);
        merged.AddRange(gateFailures);
        merged.AddRange(result.Failures);

        return result
            .WithFailures(merged)
            .WithPermanentlyDeleted([.. permanentlyDeleted, .. result.PermanentlyDeletedPaths]);
    }

    /// <summary>
    /// Raises what the single-path caller is told, from the outcome the batch came back with.
    /// </summary>
    /// <remarks>
    /// Takes the whole outcome rather than the first failure because one shape of unsuccessful result
    /// carries no failure to name: an item that had to be moved aside to reach the Shell and could be
    /// neither recycled nor moved back is the caller's data at a path they never chose, and nothing was
    /// recorded against any path. Reading <c>FirstFailure</c> there would fault on a null, which says
    /// nothing about what happened.
    /// </remarks>
    private static void ThrowForSinglePath(RecycleBinResult result)
    {
        if (result.FirstFailure is not { } failure)
        {
            RecycleBinThrowHelper.ThrowItemNotPutBack(result.LeftoverStagingDirectories);
            return;
        }

        switch (failure.Stage)
        {
            case RecycleFailureStage.LongPathNotRecyclable:
                RecycleBinThrowHelper.ThrowPathTooLong(failure);
                break;

            case RecycleFailureStage.VolumeGuidPathNotAddressable:
                RecycleBinThrowHelper.ThrowVolumeNotAddressableByGuid(failure);
                break;

            case RecycleFailureStage.PathValidation when !string.IsNullOrEmpty(failure.Path):
                RecycleBinThrowHelper.ThrowFileNotFound(failure.Path);
                break;

            default:
                RecycleBinThrowHelper.ThrowRecycleFailed(failure);
                break;
        }
    }

    private static (List<RecycleTarget> Targets, List<RecycleFailure> Failures, List<string> PermanentlyDeleted) BuildTargets(
        IReadOnlyList<string?> filePaths,
        RecycleBinOptions options)
    {
        var targets = new List<RecycleTarget>(filePaths.Count);
        var failures = new List<RecycleFailure>();
        var permanentlyDeleted = new List<string>();

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

        RefuseVolumesThatCannotRecycle(targets, failures, permanentlyDeleted, options);

        return (targets, failures, permanentlyDeleted);
    }

    /// <summary>
    /// Takes every target whose volume cannot recycle it out of the list the Shell will be handed, then
    /// either records one failure per refused path or removes the paths the caller asked to have removed.
    /// </summary>
    /// <remarks>
    /// The refusal happens here rather than after the operation because there is nothing left to refuse
    /// afterwards: the Shell removes a file for good on a volume with no Recycle Bin and reports the
    /// delete as a success, so a caller told <see cref="RecycleBinResult.Succeeded"/> would be told the
    /// opposite of what happened. Refused paths never reach the worker at all, which is what makes that
    /// structural rather than a check somebody can reorder.
    /// <para>
    /// A caller's <see cref="NoRecycleBinAction.DeletePermanently"/> is applied here too, for the same
    /// reason and by the same route: through the Shell that removal would come back as a recycle of a file
    /// that no longer exists, so the removal is this library's own and the two lists can be told apart.
    /// </para>
    /// <para>
    /// Each distinct volume root is inspected once, so a batch of twenty thousand files on one volume
    /// pays for one probe rather than twenty thousand, and nothing is remembered afterwards: a mount or a
    /// policy can change between two operations, and a remembered answer would be a stale one presented
    /// as a current one. Deciding per root is also what keeps one unusable volume from deciding the
    /// batch — every other root's paths are carried through untouched.
    /// </para>
    /// </remarks>
    /// <param name="targets">Validated targets, reduced to the ones the Shell may be handed.</param>
    /// <param name="failures">
    /// Collects one failure per refused path and one per path the file system would not remove, after the
    /// validation failures. A path listed twice is acted on once, as
    /// <see cref="RecycleOperationRunner.CollapseEquivalentPaths"/> would have recycled it once.
    /// </param>
    /// <param name="permanentlyDeleted">
    /// Collects the paths removed outright rather than handed over, which is what
    /// <see cref="NoRecycleBinAction.DeletePermanently"/> asks for on a volume this library will not
    /// recycle on the caller's behalf. Empty under either other action.
    /// </param>
    /// <param name="options">Carries the caller's choice for a volume that cannot recycle.</param>
    private static void RefuseVolumesThatCannotRecycle(
        List<RecycleTarget> targets,
        List<RecycleFailure> failures,
        List<string> permanentlyDeleted,
        RecycleBinOptions options)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var byVolumeRoot = new Dictionary<string, List<RecycleTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var root = Path.GetPathRoot(target.ShellPath) ?? string.Empty;

            if (!byVolumeRoot.TryGetValue(root, out var group))
            {
                byVolumeRoot[root] = group = [];
            }

            group.Add(target);
        }

        HashSet<string>? refused = null;
        HashSet<string>? deleted = null;

        foreach (var (root, group) in byVolumeRoot)
        {
            // A path that names no volume root names no volume, so there is no question to ask and
            // nothing to refuse. It goes to the Shell as it did before this gate existed, and the Shell
            // refuses it in its own words.
            if (root.Length == 0)
            {
                continue;
            }

            var status = RecycleBinVolumeState.Inspect(root);

            if (!RecycleBinVolumePolicy.RefusesRecycle(status.Reason, options.NoRecycleBinAction))
            {
                if (DeletesPermanentlyHere(status.Reason, options.NoRecycleBinAction))
                {
                    deleted ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    RemoveOutright(group, deleted, permanentlyDeleted, failures);
                }

                continue;
            }

            // Asked once per volume, not once per path: every target under this root is refused for the
            // same reason and carries the same code.
            var hResult = RefusalHResult(root, status.Reason);

            foreach (var target in group)
            {
                // The first spelling of a path is the one reported, because that is what
                // RecycleOperationRunner.CollapseEquivalentPaths keeps when the Shell is reached instead.
                // Recording the second would make a caller who listed a path twice see two failures for
                // one file, which the runner would never have done with the same batch.
                if (!(refused ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(target.ShellPath))
                {
                    continue;
                }

                failures.Add(new RecycleFailure(
                    target.InputPath,
                    hResult,
                    attempts: 0,
                    RecycleFailureStage.NoRecycleBinOnVolume));
            }
        }

        // Removed by location rather than rebuilt, so the surviving targets stay in the order the caller
        // supplied them and the worker sees a batch shaped exactly as it did before the gate.
        if (refused is not null)
        {
            targets.RemoveAll(target => refused.Contains(target.ShellPath));
        }

        if (deleted is not null)
        {
            targets.RemoveAll(target => deleted.Contains(target.ShellPath));
        }
    }

    /// <summary>
    /// Whether the caller's own answer asks for the paths on this volume to be removed here, rather than
    /// refused or handed over.
    /// </summary>
    /// <remarks>
    /// Both conditions are needed. The caller must have declined the refusal, which rules out
    /// <see cref="NoRecycleBinAction.AllowIndeterminate"/>: that action asks for the Shell to be asked,
    /// and the file it loses there is lost on the Shell's report rather than on this library's account.
    /// <para>
    /// And the volume must be one this library could not recycle on the caller's behalf. An
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/> volume refused nothing, so a path on it belongs to
    /// the Shell whatever the caller said about bins — including when the volume is the one hosting the
    /// caller's own data.
    /// </para>
    /// </remarks>
    private static bool DeletesPermanentlyHere(RecycleBinAvailabilityReason reason, NoRecycleBinAction action) =>
        action == NoRecycleBinAction.DeletePermanently && reason != RecycleBinAvailabilityReason.Enabled;

    /// <summary>
    /// Removes every path in <paramref name="group"/> for good, recording the ones that went and a failure
    /// for each one the file system would not remove.
    /// </summary>
    /// <param name="group">Every target on one volume root, in batch order.</param>
    /// <param name="removed">
    /// Collects the shell paths acted on, so the batch can be reduced to what the Shell is left to do.
    /// The first spelling of a path is the one acted on, so a caller who listed one path twice reads one
    /// removal and one record — what <see cref="RecycleOperationRunner.CollapseEquivalentPaths"/> would
    /// have recycled once.
    /// </param>
    /// <param name="permanentlyDeleted">Receives the caller's own spelling of each path confirmed gone.</param>
    /// <param name="failures">Receives one failure per path the file system refused to remove.</param>
    private static void RemoveOutright(
        List<RecycleTarget> group,
        HashSet<string> removed,
        List<string> permanentlyDeleted,
        List<RecycleFailure> failures)
    {
        foreach (var target in group)
        {
            if (!removed.Add(target.ShellPath))
            {
                continue;
            }

            if (PermanentDeleter.TryDelete(target, permanentlyDeleted, out var hResult))
            {
                continue;
            }

            // A refusal carrying no code of its own is still a refusal, and there is nowhere else for the
            // path to go: on this volume the Shell would destroy it and report a recycle. Saying so is the
            // whole answer available, and saying it as PermanentDeleteFailed keeps the reason the caller
            // gave visible rather than blaming the volume they were never asked about.
            failures.Add(new RecycleFailure(target.InputPath, hResult, attempts: 0, RecycleFailureStage.PermanentDeleteFailed));
        }
    }

    /// <summary>
    /// The code a refusal carries: the Shell's own where the Shell named the volume, and nothing where the
    /// refusal was settled from configuration.
    /// </summary>
    /// <remarks>
    /// Only <see cref="RecycleBinAvailabilityReason.NoRecycleBinOnVolume"/> is a refusal the Shell
    /// itself stated, so it is the only reason worth asking it a second time about — and the only one with
    /// a code to carry. The rest are settled from configuration, which is exactly what
    /// <see cref="RecycleFailure.HResult"/> documents as <c>0</c>: there is no code of the Shell's to
    /// report. <see cref="RecycleBinAvailabilityReason.Indeterminate"/> is in that group even though its
    /// probe did reach the Shell, because the probe found a bin — a volume the Shell finds a bin on has
    /// no failure code to hand over.
    /// </remarks>
    private static int RefusalHResult(string volumeRoot, RecycleBinAvailabilityReason reason)
    {
        if (reason != RecycleBinAvailabilityReason.NoRecycleBinOnVolume)
        {
            return 0;
        }

        var hResult = RecycleBinCounter.QueryHResult(volumeRoot);

        // A bin that appeared between the probe and this query means the reason the record was made for
        // no longer holds. Reporting 0 rather than a success code is the honest answer to "what did the
        // Shell refuse with" — it refused nothing — and 0 is what a caller already reading the severity
        // bit is told to expect from a refusal the Shell never made.
        return hResult < 0 ? hResult : 0;
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

        // Checked on the normalised path as well as on what the caller typed, because
        // Path.GetFullPath rewrites forward slashes to backslashes, which turns "//?/Volume{...}" into
        // the device form. A check made only on the caller's spelling would let it through, to be handed
        // to a Shell that refuses it and retried for every attempt the caller asked for.
        if (RecycleTarget.IsVolumeGuidPath(rawPath) || RecycleTarget.IsVolumeGuidPath(fullPath))
        {
            return new RecycleFailure(rawPath, 0, 0, RecycleFailureStage.VolumeGuidPathNotAddressable);
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
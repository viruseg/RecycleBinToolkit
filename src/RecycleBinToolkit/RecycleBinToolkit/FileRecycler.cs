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
/// this library and named in <see cref="RecycleBinResult.PermanentlyDeletedPaths"/>. That gate runs
/// before a backend is chosen, so it holds the same way on either one.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
public static class FileRecycler
{
    /// <summary>
    /// How many paths a batch is validated through between two looks at the caller's token. Small enough
    /// that a cancelled scan stops at once on any batch a caller would recognise as large, large enough
    /// that the check is not a measurable share of a loop whose body blocks on the filesystem.
    /// </summary>
    private const int CancellationCheckInterval = 64;

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
    /// Cancels the scan that validates the batch and the work between paths. A cancelled batch stops
    /// part way and raises without returning a result: which files went is found by reading the bin.
    /// The validation scan is checked too, because on a large batch it is the long half of the call
    /// and it is over before the first move would have begun.
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
    /// Cancels the scan that validates the batch and the work between paths. A cancelled batch stops
    /// part way and raises without returning a result: which files went is found by reading the bin.
    /// The validation scan is checked too, because on a large batch it is the long half of the call
    /// and it is over before the first move would have begun.
    /// </param>
    /// <returns>
    /// The outcome of the batch. Never throws for a per-item failure; read
    /// <see cref="RecycleBinResult.Failures"/> for the diagnostics. A path refused because its volume
    /// cannot recycle it arrives there at stage <see cref="RecycleFailureStage.NoRecycleBinOnVolume"/>,
    /// and the paths on every other volume in the batch are still recycled.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled, whether before the work started or while the
    /// batch was being validated.
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

        // Before argument validation, before any filesystem question, and before the hop to the pool: the
        // machine either can be written to or cannot, and that does not depend on the paths, so a
        // caller is told before this library starts touching anything. It is not a RecycleFailure and
        // ThrowOnError does not suppress it — nothing was attempted, so there is nothing to report as data.
        RecycleBinBackendGuard.RequireWriteSupport();

        // Validating a batch is one filesystem probe per path and, on the volume measured here, about
        // 11 ms each — so 20,000 paths is a little over three and a half minutes. Run inline that time is
        // the caller's own, because an async method runs everything before its first await on the
        // thread that called it: a caller on a UI thread would be frozen for the whole scan, and a caller
        // holding a lock would hold it. Hopping to the pool first costs a scheduling round trip and buys
        // back the caller's thread, which is the only one that has something else to be doing.
        return await Task
            .Run(() => RecycleBatch(filePaths, effective, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a batch and moves it, on one pool thread.
    /// </summary>
    /// <remarks>
    /// One trip rather than two. Validation is minutes of filesystem work on a large batch and belongs on
    /// the pool; the recycle is seconds of file moves. Running them on the same thread costs nothing and
    /// saves a scheduling round trip.
    /// </remarks>
    /// <returns>The outcome, with the volume gate's own verdicts merged in.</returns>
    private static RecycleBinResult RecycleBatch(
        IReadOnlyList<string?> filePaths,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        var (targets, gateFailures, permanentlyDeleted) = BuildTargets(filePaths, options, cancellationToken);

        if (targets.Count == 0)
        {
            return RecycleBinResult.Create([], permanentlyDeleted, gateFailures, TimeSpan.Zero);
        }

        var result = NativeRecycleRunner.Run(targets, options, cancellationToken);

        if (gateFailures.Count == 0 && permanentlyDeleted.Count == 0)
        {
            return result;
        }

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
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        var targets = new List<RecycleTarget>(filePaths.Count);
        var failures = new List<RecycleFailure>();
        var permanentlyDeleted = new List<string>();

        for (var i = 0; i < filePaths.Count; i++)
        {
            // Asked every so often rather than per path. The probe below blocks on the filesystem for
            // milliseconds, so a caller who cancels during a twenty-thousand-path scan waits minutes for an
            // answer they already gave — and asking costs a predictable branch, which is why the interval
            // is a count rather than a stopwatch: a scan of a slow volume must not turn into a scan of a
            // clock.
            if (i % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

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

        // Before the volume gate rather than inside it, because the gate is where a cancelled batch would
        // otherwise still do damage: under NoRecycleBinAction.DeletePermanently it removes the caller's
        // files outright, and it answers one volume at a time rather than one path, so a check inside its
        // loop would be checking once for the whole batch anyway.
        cancellationToken.ThrowIfCancellationRequested();

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
    /// <param name="targets">Validated targets, reduced to the ones that may be recycled.</param>
    /// <param name="failures">
    /// Collects one failure per refused path and one per path the file system would not remove, after the
    /// validation failures. A path listed twice is acted on once, as
    /// <see cref="RecycleTarget.CollapseEquivalent"/> would have recycled it once.
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
            // same reason. The refusal is settled from configuration, so it carries no code: 0 is what
            // RecycleFailure.HResult documents for a refusal nothing reported.
            const int hResult = 0;

            foreach (var target in group)
            {
                // The first spelling of a path is the one reported, because that is what
                // RecycleTarget.CollapseEquivalent keeps. Recording the second would make a caller
                // who listed a path twice see two failures for one file.
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
    /// Collects the paths acted on, so the batch can be reduced to what is left to do.
    /// The first spelling of a path is the one acted on, so a caller who listed one path twice reads one
    /// removal and one record — what <see cref="RecycleTarget.CollapseEquivalent"/> would
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

        if (!TryDescribe(fullPath, out var attributes, out var size))
        {
            return new RecycleFailure(rawPath, 0, 0, RecycleFailureStage.PathValidation);
        }

        target = new RecycleTarget(rawPath, fullPath, RecycleTarget.StripExtendedPrefix(fullPath), attributes, size);
        return null;
    }

    /// <summary>
    /// Whether a path is there at all, file or directory, and what it is.
    /// </summary>
    /// <param name="path">Full path to describe.</param>
    /// <param name="attributes">Receives what the filesystem said, or <c>Directory</c> when only a probe knows.</param>
    /// <param name="size">Receives the item's length, or 0 for a directory and for a path that could not be measured.</param>
    /// <returns><see langword="true"/> when the path is there.</returns>
    /// <remarks>
    /// One question, answering everything validation needs. <c>File.Exists(a) || Directory.Exists(a)</c>
    /// costs two filesystem probes for every directory in the batch, because the file probe has to fail
    /// first, and it is asked once per path before the Shell is involved — 20,000 paths measured at 11 ms
    /// each, which is 220 seconds of the caller's thread before the first await.
    /// <para>
    /// <see cref="FileInfo"/> rather than <see cref="File.GetAttributes(string)"/>, and that is the whole reason
    /// the length comes for free: one status query reports attributes, size and timestamps together, and a
    /// separate call for the length would be a second round trip for a number the first one already
    /// carried. The native backend writes that number into the Recycle Bin's index record, so a batch of
    /// twenty thousand files pays for twenty thousand queries rather than forty thousand.
    /// </para>
    /// <para>
    /// It throws where the probes returned false, so the fallbacks are what keep a path the file system
    /// would not describe from being reported as absent — and they report the answer they can.
    /// </para>
    /// </remarks>
    private static bool TryDescribe(string path, out FileAttributes attributes, out long size)
    {
        try
        {
            var info = new FileInfo(path);
            attributes = info.Attributes;

            // Existence is asked of the info object rather than inferred from its attributes, and the reason
            // is a sharp edge worth writing down: for a path that is not there, Attributes answers
            // (FileAttributes)(-1) instead of throwing — every bit set, which includes the Directory bit.
            // Reading that as "a directory" would validate every missing path on the machine as an existing
            // one. Exists reads the same cached status, so asking it costs nothing.
            if (!info.Exists)
            {
                return DescribeByProbing(path, out attributes, out size);
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                size = 0;
                return true;
            }

            size = info.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A path the status call could not measure at all — held, denied, or on a volume that is not
            // mounted. The probes are what keep it from being reported as absent.
            return DescribeByProbing(path, out attributes, out size);
        }
    }

    /// <summary>
    /// Answers for a path the status call could not describe, by asking each kind in turn.
    /// </summary>
    /// <remarks>
    /// A file the status call could not measure is still there if a probe says so, and the probe does not say
    /// what kind of thing it is — so the answer is whichever one it can answer, and a path measured this way
    /// reports a size of zero rather than a guess. Two probes is the price of that answer, and it is paid
    /// only by the paths that need it: a volume that is not mounted, a path somebody holds in a way the
    /// filesystem will not describe.
    /// </remarks>
    private static bool DescribeByProbing(string path, out FileAttributes attributes, out long size)
    {
        size = 0;

        if (File.Exists(path))
        {
            attributes = default;
            return true;
        }

        if (Directory.Exists(path))
        {
            attributes = FileAttributes.Directory;
            return true;
        }

        attributes = default;
        return false;
    }
}
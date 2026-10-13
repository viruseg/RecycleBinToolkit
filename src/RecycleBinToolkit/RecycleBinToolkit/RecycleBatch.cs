using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Turns a caller's list of paths into the targets <see cref="NativeRecycleRunner"/> moves, having first
/// settled every path whose volume cannot recycle it.
/// </summary>
/// <remarks>
/// The half of a recycle that is this library's own judgement rather than the Recycle Bin's: a path is
/// described once, a volume is asked once, and everything that must not be moved is taken out of the
/// list before the first move is attempted. <see cref="RecycleBin.RecycleAsync(IReadOnlyList{string?},
/// RecycleOptions?, CancellationToken)"/> owns the arguments, the hop to the thread pool, and how the
/// outcome is handed back; this owns what happens on the far side of it.
/// <para>
/// Runs on one pool thread, so a caller is not charged for the validation scan on its own thread — which,
/// on a batch of twenty thousand paths, is minutes of filesystem probes.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class RecycleBatch
{
    /// <summary>
    /// How many paths a batch is validated through between two looks at the caller's token. Small enough
    /// that a cancelled scan stops at once on any batch a caller would recognise as large, large enough
    /// that the check is not a measurable share of a loop whose body blocks on the filesystem.
    /// </summary>
    private const int CancellationCheckInterval = 64;

    /// <summary>
    /// Validates a batch and moves it, on one pool thread.
    /// </summary>
    /// <remarks>
    /// One trip rather than two. Validation is minutes of filesystem work on a large batch and belongs on
    /// the pool; the recycle is seconds of file moves. Running them on the same thread costs nothing and
    /// saves a scheduling round trip.
    /// </remarks>
    /// <returns>The outcome, with the volume gate's own verdicts merged in.</returns>
    internal static RecycleBinResult Run(
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
    /// Takes every target whose volume cannot recycle it out of the list to be moved, then
    /// either records one failure per refused path or removes the paths the caller asked to have removed.
    /// </summary>
    /// <remarks>
    /// The refusal happens here rather than after the operation because there is nothing left to refuse
    /// afterwards: without it a caller told <see cref="RecycleBinResult.Succeeded"/> would be told the
    /// opposite of what happened. Refused paths are never attempted at all, which is what makes that
    /// structural rather than a check somebody can reorder.
    /// <para>
    /// A caller's <see cref="NoRecycleBinAction.DeletePermanently"/> is applied here too: the removal is
    /// this library's own, so a recycle and a removal are never confused and the two lists can be told
    /// apart.
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
            var root = Path.GetPathRoot(target.FullPath) ?? string.Empty;

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
            // nothing to refuse. It is carried through to the move, which refuses it in its own words
            // when the root still names nothing.
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
                if (!(refused ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(target.FullPath))
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
        // supplied them.
        if (refused is not null)
        {
            targets.RemoveAll(target => refused.Contains(target.FullPath));
        }

        if (deleted is not null)
        {
            targets.RemoveAll(target => deleted.Contains(target.FullPath));
        }
    }

    /// <summary>
    /// Whether the caller's own answer asks for the paths on this volume to be removed here, rather than
    /// refused or handed over.
    /// </summary>
    /// <remarks>
    /// Both conditions are needed. The caller must have declined the refusal, which rules out
    /// <see cref="NoRecycleBinAction.AllowIndeterminate"/>: that action asks for the path to be recycled
    /// anyway.
    /// <para>
    /// And the volume must be one this library could not recycle on the caller's behalf. An
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/> volume refused nothing, so a path on it is
    /// recycled whatever the caller said about bins — including when the volume is the one hosting the
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
            if (!removed.Add(target.FullPath))
            {
                continue;
            }

            if (PermanentDeleter.TryDelete(target, permanentlyDeleted, out var hResult))
            {
                continue;
            }

            // A refusal carrying no code of its own is still a refusal, and there is nowhere else for the
            // path to go. Saying so is the whole answer available, and saying it as PermanentDeleteFailed
            // keeps the reason the caller gave visible rather than blaming the volume they were never
            // asked about.
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
        // the device form. A check made only on the caller's spelling would let it through, to be refused
        // and retried for every attempt the caller asked for.
        if (RecycleTarget.IsVolumeGuidPath(rawPath) || RecycleTarget.IsVolumeGuidPath(fullPath))
        {
            return new RecycleFailure(rawPath, 0, 0, RecycleFailureStage.VolumeGuidPathNotAddressable);
        }

        if (!TryDescribe(fullPath, out var attributes, out var size))
        {
            return new RecycleFailure(rawPath, 0, 0, RecycleFailureStage.PathValidation);
        }

        target = new RecycleTarget(rawPath, fullPath, attributes, size);
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
    /// first, and it is asked once per path before the move.
    /// <para>
    /// <see cref="FileInfo"/> rather than <see cref="File.GetAttributes(string)"/>, and that is the whole reason
    /// the length comes for free: one status query reports attributes, size and timestamps together, and a
    /// separate call for the length would be a second round trip for a number the first one already
    /// carried.
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
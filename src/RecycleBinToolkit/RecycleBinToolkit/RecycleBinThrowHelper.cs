using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace RecycleBinToolkit;

/// <summary>
/// Every throw site in this assembly, one method per failure mode, following the
/// <c>ThrowIf</c>/<c>Throw</c> convention of the BCL.
/// </summary>
/// <remarks>
/// Messages and exception objects are built only when a failure actually happens, so the success
/// path allocates nothing and carries no exception plumbing.
/// </remarks>
internal static class RecycleBinThrowHelper
{
    /// <summary>
    /// Reports a path that does not exist.
    /// </summary>
    internal static void ThrowFileNotFound(string fullPath) =>
        throw new FileNotFoundException($"File or directory not found: '{fullPath}'.", fullPath);

    /// <summary>
    /// Reports an item that never reached the Recycle Bin, preserving the Shell error code both on
    /// the exception and on its <see cref="Exception.InnerException"/>.
    /// </summary>
    internal static void ThrowRecycleFailed(RecycleFailure failure) =>
        throw new RecycleFailedException(failure);

    /// <summary>
    /// Reports that the Shell cannot recycle a path, leaving the file untouched.
    /// </summary>
    internal static void ThrowPathTooLong(RecycleFailure failure) =>
        throw new PathTooLongException(
            $"'{failure.Path}' is longer than the Shell can recycle, and the volume offers no short name for it. " +
            "The file was left in place. Set LongPathFailureAction to DeletePermanently to remove it instead.");

    /// <summary>
    /// Reports a path that names its volume by GUID, which nothing above this library can read.
    /// </summary>
    /// <remarks>
    /// A different answer from every other one here, because nothing failed: the Shell was never asked,
    /// so there is no code of its to carry and no code is carried. What there is is a spelling of the same
    /// location that works, and saying so is the whole answer.
    /// </remarks>
    [DoesNotReturn]
    internal static void ThrowVolumeNotAddressableByGuid(RecycleFailure failure) =>
        throw new NotSupportedException(
            $"'{failure.Path}' names its volume by GUID, which the Shell will not parse and this library " +
            "cannot measure, so it was refused before either was asked. Address the same location by drive " +
            "letter or mount point instead, such as \"X:\\path\".");

    /// <summary>
    /// Reports that the Recycle Bin worker thread could not be brought up, so no operation can be performed.
    /// </summary>
    /// <remarks>
    /// Marked as not returning because it only ever throws. That is what lets the caller keep the flow it
    /// has rather than a branch on an exception that cannot be caught and handled locally.
    /// </remarks>
    [DoesNotReturn]
    internal static void ThrowWorkerUnavailable(Exception? inner) =>
        throw new InvalidOperationException(
            "The Recycle Bin worker thread could not be initialised.",
            inner);

    /// <summary>
    /// Reports a staging directory the operation left on a volume.
    /// </summary>
    /// <remarks>
    /// A different failure from every other one here: nothing was refused, and no path failed, so there is
    /// no code to carry and no path to name. What there is is a directory, and naming it is the whole
    /// answer. Every one is named, because a batch spanning volumes stages per volume and a leftover on
    /// one does not make a leftover on another go away.
    /// <para>
    /// The wording is deliberately agnostic about whether the directory still holds anything, because
    /// the library cannot tell the caller: one that could not be removed is reported as leftover whether
    /// or not the move back took, and both look identical from here. Naming the directory and saying
    /// nothing was deleted is true either way.
    /// </para>
    /// </remarks>
    [DoesNotReturn]
    internal static void ThrowItemNotPutBack(IReadOnlyList<string> leftoverStagingDirectories) =>
        throw new InvalidOperationException(
            $"A staging directory was left on the volume: {string.Join(" and ", leftoverStagingDirectories.Select(d => $"'{d}'"))}. " +
            "Nothing was deleted. It either holds an item that could not be moved back to where it was, or it is empty " +
            "and could not be removed; look inside it before moving on.");

    /// <summary>
    /// Reports that this machine is too old for the native backend to write to its Recycle Bin.
    /// </summary>
    /// <remarks>
    /// Marked as not returning because it only ever throws. Nothing was attempted, so unlike every other
    /// throw here there is no path to name and no code to carry: the answer is about the machine rather than
    /// about anything the caller did.
    /// </remarks>
    [SupportedOSPlatform("windows6.0.6000")]
    [DoesNotReturn]
    internal static void ThrowNativeBackendUnavailable(int major, int minor, int build) =>
        throw new RecycleBinNativeBackendUnavailableException(major, minor, build);
}

/// <summary>
/// Raised when the native backend is asked to recycle on a machine whose Shell cannot read what it writes.
/// </summary>
/// <remarks>
/// A different failure from every other one here, and not suppressible. Nothing was attempted: the refusal
/// is about the operating system rather than about a path, so there is no path to report and no
/// <see cref="RecycleFailureStage"/> that describes it, which is why
/// <see cref="RecycleBinOptions.ThrowOnError"/> does not turn it into data. Suppression applies to failures
/// of items; there was no item.
/// <para>
/// The alternative is a silent loss. A version 2 index record written on a machine whose Shell reads only
/// version 1 is not rejected — it is read as something the Shell cannot use, so the item appears in the bin
/// and cannot be restored from it. That is the outcome this exception exists to prevent.
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
internal sealed class RecycleBinNativeBackendUnavailableException : PlatformNotSupportedException
{
    internal RecycleBinNativeBackendUnavailableException(int major, int minor, int build)
        : base(
            $"The Native Recycle Bin backend needs Windows 10 version 1607 (build {RecycleBinBackendGuard.MinimumBuild}) or later, " +
            $"because it writes Recycle Bin index records of version 2 and no older Shell can read those. " +
            $"This machine is Windows {major}.{minor} build {build}. Nothing was recycled and no file was touched. " +
            "Use RecycleBinBackend.WinApi on this system, or run on a newer Windows.")
    {
    }
}

/// <summary>
/// Raised when an operation failed outright and left the caller's own data in a staging directory it
/// could not be moved back from.
/// </summary>
/// <remarks>
/// <b>A cancelled caller usually does not see this.</b> Cancelling ends the wait rather than the work,
/// and the wait ends with an <see cref="OperationCanceledException"/> raised by the caller's own token —
/// the worker goes on regardless, and its outcome is read by nobody. That is the documented contract of
/// cancellation, and it means a batch cancelled after staging can strand an item without the caller
/// being able to learn where from anything it is handed. The two race rather than one always winning,
/// so a cancellation arriving at the same moment as the failure can surface this instead. Where it does
/// surface, it is for the failures that reach the caller: a Shell fault escaping the rounds, an
/// unrecreatable operation object, or the single-path overload with
/// <see cref="RecycleBinOptions.ThrowOnError"/> on. Cancelled or not, a directory named <c>$RCW-</c> on
/// the volume root is worth looking for.
/// </remarks>
internal sealed class RecycleBinItemLeftBehindException : IOException
{
    internal RecycleBinItemLeftBehindException(IReadOnlyList<string> leftoverStagingDirectories, Exception inner)
        : base(
            $"The operation failed with a staging directory left on the volume: {string.Join(" and ", leftoverStagingDirectories.Select(d => $"'{d}'"))}. " +
            "Nothing was deleted. One of these still holds an item of yours at a short path rather than where it was, " +
            "or it is empty and could not be removed; look inside before moving on.",
            inner)
    {
        LeftoverStagingDirectories = leftoverStagingDirectories;
    }

    /// <summary>The directories left on the volume, in the order they were created.</summary>
    public IReadOnlyList<string> LeftoverStagingDirectories { get; }
}

/// <summary>
/// Raised when an item could not be moved to the Recycle Bin within the configured attempts.
/// </summary>
internal sealed class RecycleFailedException : IOException
{
    internal RecycleFailedException(RecycleFailure failure)
        : base(
            BuildMessage(failure),
            // The Shell's own code is carried as an inner exception because it is the only exception type
            // that takes an HRESULT verbatim, and it never escapes: it is always inside this IOException,
            // whose own HResult is the Shell's code as well.
#pragma warning disable CA2201 // Reserved exception type, used only as an HRESULT carrier.
            new COMException(BuildComMessage(failure), failure.HResult)
#pragma warning restore CA2201
        )
    {
        HResult = failure.HResult;
        Failure = failure;
    }

    /// <summary>The failure record this exception was built from.</summary>
    public RecycleFailure Failure { get; }

    private static string BuildMessage(RecycleFailure failure) =>
        $"Could not move to the Recycle Bin after {failure.Attempts} attempt(s): " +
        $"stage={failure.Stage}, hresult=0x{failure.HResult:X8}, path='{failure.Path}'.";

    private static string BuildComMessage(RecycleFailure failure) =>
        $"The Shell reported 0x{failure.HResult:X8} for '{failure.Path}' at stage {failure.Stage}.";
}
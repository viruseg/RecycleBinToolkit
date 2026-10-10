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
    /// Reports an item that never reached the Recycle Bin, preserving the error code both on
    /// the exception and on its <see cref="Exception.InnerException"/>.
    /// </summary>
    internal static void ThrowRecycleFailed(RecycleFailure failure) =>
        throw new RecycleFailedException(failure);

    /// <summary>
    /// Reports a path that names its volume by GUID, which nothing above this library can read.
    /// </summary>
    /// <remarks>
    /// A different answer from every other one here, because nothing failed: nothing was attempted,
    /// so there is no code to carry and no code is carried. What there is is a spelling of the same
    /// location that works, and saying so is the whole answer.
    /// </remarks>
    [DoesNotReturn]
    internal static void ThrowVolumeNotAddressableByGuid(RecycleFailure failure) =>
        throw new NotSupportedException(
            $"'{failure.Path}' names its volume by GUID, which this library cannot measure, empty or " +
            "report against, so it was refused before anything was asked. Address the same location by drive " +
            "letter or mount point instead, such as \"X:\\path\".");

    /// <summary>
    /// Reports that this machine is too old to write to its Recycle Bin.
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
/// Raised when the Recycle Bin cannot be written to on this machine.
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
            $"The Recycle Bin needs Windows 10 version 1607 (build {RecycleBinBackendGuard.MinimumBuild}) or later, " +
            $"because it writes Recycle Bin index records of version 2 and no older Shell can read those. " +
            $"This machine is Windows {major}.{minor} build {build}. Nothing was recycled and no file was touched. " +
            "Run on a newer Windows.")
    {
    }
}

/// <summary>
/// Raised when an item could not be moved to the Recycle Bin within the configured attempts.
/// </summary>
internal sealed class RecycleFailedException : IOException
{
    internal RecycleFailedException(RecycleFailure failure)
        : base(
            BuildMessage(failure),
            // The file system's own code is carried as an inner exception because it is the only exception
            // type that takes an HRESULT verbatim, and it never escapes: it is always inside this
            // IOException, whose own HResult is the same code.
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
        $"The file system reported 0x{failure.HResult:X8} for '{failure.Path}' at stage {failure.Stage}.";
}
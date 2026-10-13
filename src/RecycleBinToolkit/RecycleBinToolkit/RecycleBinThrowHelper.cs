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
    /// Reports a path the Recycle Bin's index record cannot name.
    /// </summary>
    /// <remarks>
    /// Its own exception type rather than the general failure, because a caller meeting this can do
    /// something about it: a shorter path, a different name for one of its directories, or
    /// <see cref="LongPathFailureAction"/> set to something other than <see cref="LongPathFailureAction.Throw"/>.
    /// Nothing was moved and the file is where it was.
    /// </remarks>
    [DoesNotReturn]
    internal static void ThrowPathTooLong(RecycleFailure failure) =>
        throw new PathTooLongException(
            $"'{failure.Path}' cannot be recorded in the Recycle Bin on this machine, because its index record has a fixed-size " +
            $"path field. The file was left in place. Set LongPathFailureAction to DeletePermanently to remove it instead, " +
            "or move it somewhere shorter.");
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
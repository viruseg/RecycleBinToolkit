using System.Diagnostics.CodeAnalysis;
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
using System.Runtime.InteropServices;

namespace Utils;

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
    /// Reports that the Shell worker could not be brought up, so no operation can be performed.
    /// </summary>
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
        : base(BuildMessage(failure), new COMException(BuildComMessage(failure), failure.HResult))
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
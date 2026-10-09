using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>
/// Picks the backend a call goes to.
/// </summary>
/// <remarks>
/// One place, so that no caller ever chooses for itself and no call can reach a backend its options
/// did not name. Nothing about the choice is inferred from the machine — a caller that did not ask
/// for the native path does not get it, on a volume where it would be faster.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RecycleBinBackends
{
    /// <summary>
    /// The backend <paramref name="options"/> names.
    /// </summary>
    /// <param name="options">The caller's settings; <see langword="null"/> means the documented defaults.</param>
    /// <returns>The implementation that carries out the call.</returns>
    /// <remarks>
    /// Every value of <see cref="RecycleBinBackend"/> answers here, so adding a backend is a change
    /// in one place rather than a branch in every caller.
    /// </remarks>
    internal static IRecycleBinBackend For(RecycleBinOptions? options)
    {
        var effective = options ?? RecycleBinOptions.Default;

        // Reading and emptying do not route through here yet: the native backend has neither, so a listing
        // or an empty named for it still takes the Shell path. This switch answers for the recycle, which is
        // the operation the native backend implements.
        return effective.Backend == RecycleBinBackend.Native
            ? NativeRecycleBinBackend.Instance
            : ShellRecycleBinBackend.Instance;
    }
}
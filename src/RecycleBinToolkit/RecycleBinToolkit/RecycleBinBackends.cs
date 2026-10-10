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
    /// in one place rather than a branch in every caller. A value this version does not define falls to
    /// the Shell, which is the contract <see cref="RecycleBinOptions.Backend"/> documents and the one
    /// both the reading and the writing paths rely on — neither goes through
    /// <see cref="RecycleBinOptions.Normalize"/> on the way here.
    /// </remarks>
    internal static IRecycleBinBackend For(RecycleBinOptions? options)
    {
        var effective = options ?? RecycleBinOptions.Default;

        // Every operation both backends implement is routed here, which is what makes the boundary checkable
        // by reading: only ShellRecycleBinBackend names RecycleBinWorker, and nothing else in the assembly
        // can reach it.
        return effective.Backend == RecycleBinBackend.Native
            ? NativeRecycleBinBackend.Instance
            : ShellRecycleBinBackend.Instance;
    }
}
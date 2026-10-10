using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace RecycleBinToolkit;

/// <summary>
/// Decides whether this machine's Recycle Bin can be written to at all.
/// </summary>
/// <remarks>
/// This library writes index records of version 2. The first Windows build whose own Shell reads
/// them is <see cref="MinimumBuild"/>; below that the Shell reads a record, does not understand it, and
/// leaves the caller with an item in the bin that cannot be restored. That is not a slower outcome, it is a
/// lost one, which is why it is refused rather than warned about.
/// <para>
/// The version comes from <c>RtlGetVersion</c> rather than from <see cref="Environment.OSVersion"/> or
/// <see cref="OperatingSystem"/>. Both of those report the framework's declared minimum instead of the
/// running system whenever that minimum is below it, and this assembly declares
/// <c>windows6.0.6000</c> — so the shim would answer with exactly the version on which writing is
/// refused. The native call answers with the machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RecycleBinBackendGuard
{
    /// <summary>
    /// The first Windows build that reads a version 2 index record: 10 version 1607, the Anniversary
    /// Update.
    /// </summary>
    /// <remarks>
    /// A documented constant rather than a measured one. Nothing in a test can make a Windows machine
    /// report an older build, so what the suite pins is the other half — that the machine under test
    /// satisfies the boundary — and this value is where a disagreement about it belongs.
    /// </remarks>
    internal const int MinimumBuild = 14393;

    /// <summary>STATUS_SUCCESS, which is the only success code <c>RtlGetVersion</c> returns.</summary>
    private const int Success = 0;

    private static readonly Lazy<(int Major, int Minor, int Build)> LazyRunningVersion = new(ReadVersion);

    /// <summary>
    /// The Windows version this process is running on, as the machine reports it.
    /// </summary>
    internal static (int Major, int Minor, int Build) RunningVersion => LazyRunningVersion.Value;

    /// <summary>
    /// Refuses the write path on a machine whose Shell could not read what it writes.
    /// </summary>
    /// <remarks>
    /// The refusal is not a <see cref="RecycleFailure"/> and not something
    /// <see cref="RecycleBinOptions.ThrowOnError"/> suppresses: nothing was attempted, so there is no path
    /// to name, no stage that describes it and no code to carry. It is the answer to "can this machine do
    /// this at all", and it is the same answer whichever way the caller asked.
    /// </remarks>
    internal static void RequireWriteSupport()
    {
        var version = RunningVersion;

        if (version.Major > 10 || (version.Major == 10 && version.Build >= MinimumBuild))
        {
            return;
        }

        RecycleBinThrowHelper.ThrowNativeBackendUnavailable(version.Major, version.Minor, version.Build);
    }

    /// <summary>
    /// Asks the machine. A call that fails is reported as zero rather than as a version, because a version
    /// that could not be read has to refuse the write — and an unanswered question must never come back
    /// looking like permission.
    /// </summary>
    private static (int Major, int Minor, int Build) ReadVersion()
    {
        var version = default(OSVERSIONINFOW);
        version.dwOSVersionInfoSize = (uint)Unsafe.SizeOf<OSVERSIONINFOW>();

        // STATUS_SUCCESS is zero; NTSTATUS carries no Succeeded/Failed of its own in this projection.
        if (Windows.Wdk.PInvoke.RtlGetVersion(ref version).Value != Success)
        {
            return (0, 0, 0);
        }

        return ((int)version.dwMajorVersion, (int)version.dwMinorVersion, (int)version.dwBuildNumber);
    }
}
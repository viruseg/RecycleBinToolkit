using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

using Windows.Win32;
using Windows.Win32.System.SystemInformation;

namespace RecycleBinToolkit;

/// <summary>
/// Which of the two index record formats this machine's Recycle Bin has to be written in.
/// </summary>
/// <remarks>
/// Version 1 has a fixed 520-byte path field, which is 260 characters and no more. Version 2 replaced
/// the field with a length and carries a path of any length, so the format follows the machine: above the
/// boundary a version 2 record is written because every Shell from there reads it, and below it a
/// version 1 record, because a Shell too old to read version 2 reads a version 2 record as something it
/// cannot use and the item then appears in the bin and cannot be restored. There is nothing to refuse,
/// and that is the point — the older machine is served rather than turned away.
/// <para>
/// The caller has no say. A version 2 machine can read both formats, so choosing the older one there
/// would only lose the longer paths; and a version 1 machine can read only one, so choosing the newer
/// one there would lose the ability to restore at all.
/// </para>
/// <para>
/// The version comes from <c>RtlGetVersion</c> rather than from <see cref="Environment.OSVersion"/> or
/// <see cref="OperatingSystem"/>. Both of those report the framework's declared minimum instead of the
/// running system whenever that minimum is below it, and this assembly declares
/// <c>windows10.0.14393</c> — so the shim would answer with the oldest Windows this library claims. The
/// native call answers with the machine.
/// </para>
/// </remarks>
internal static class IndexFormat
{
    /// <summary>The format Windows Vista through 8.1 wrote, and the one this library writes below <see cref="MinimumBuildForCurrent"/>.</summary>
    internal const int Legacy = 1;

    /// <summary>The format this library writes on a machine at or above <see cref="MinimumBuildForCurrent"/>.</summary>
    internal const int Current = 2;

    /// <summary>
    /// The first Windows build whose own Shell reads a version 2 record: 10 version 1607, the Anniversary
    /// Update.
    /// </summary>
    /// <remarks>
    /// A documented constant rather than a measured one. Nothing in a test can make a Windows machine
    /// report an older build, so what the suite pins is the other half — that the machine under test
    /// agrees with the boundary rather than being refused by it.
    /// </remarks>
    internal const int MinimumBuildForCurrent = 14393;

    /// <summary>STATUS_SUCCESS, which is the only success code <c>RtlGetVersion</c> returns.</summary>
    private const int Success = 0;

    // The two members below are annotated because they reach RtlGetVersion. Nothing above this class is:
// NativeIndexFile reads the version numbers and is itself unannotated, because RecycleBinItem reads
// records through it and is a public type. Annotating the class instead would carry the requirement
// out into every one of those callers for no gain — this assembly has no non-Windows target, and every
// caller that can start an operation is already annotated.
#pragma warning disable CA1416
private static readonly Lazy<(int Major, int Minor, int Build)> LazyRunningVersion = new(ReadVersion);
#pragma warning restore CA1416

    private static int _machineQuestions;

    /// <summary>
    /// The version an operation writes in: <see cref="Legacy"/> when <see cref="ForceVersionOne"/> is set,
    /// and otherwise what this machine's Shell can read.
    /// </summary>
    /// <remarks>
    /// <see cref="ForceVersionOne"/> is read here rather than folded into the cached value, because the
    /// cache is built once per process and a caller that set the flag after that would silently get the
    /// cached answer instead.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.14393")]
    internal static int VersionForWriting =>
        ForceVersionOne
            ? Legacy
            : ReadsCurrent(RunningVersion) ? Current : Legacy;

    /// <summary>
    /// Writes a version 1 record whatever this machine's Shell would rather have.
    /// </summary>
    /// <remarks>
    /// Internal, and off. It exists so a test on a modern machine can exercise the version 1 path, which
    /// is otherwise reachable only on a machine old enough to need it. Reached by anything else it is a
    /// caller who asked for a format this library writes correctly and has tested, and got it.
    /// </remarks>
    internal static bool ForceVersionOne { get; set; }

    /// <summary>
    /// The Windows version this process is running on, as the machine reports it.
    /// </summary>
    [SupportedOSPlatform("windows10.0.14393")]
    internal static (int Major, int Minor, int Build) RunningVersion => LazyRunningVersion.Value;

    /// <summary>How many times the machine was actually asked.</summary>
    /// <remarks>
    /// Read by a case that pins the answer to being resolved once per process. A <see cref="Lazy{T}"/>
    /// measures nothing on its own, and the claim is worth nothing untested.
    /// </remarks>
    internal static int MachineQuestions => Volatile.Read(ref _machineQuestions);

    /// <summary>
    /// Whether a reported version is one whose Shell reads <see cref="Current"/>.
    /// </summary>
    /// <remarks>
    /// Anything above major 10 counts. A version this library was not written against is newer, not
    /// older, and a version that could not be read reports zeros and is answered here as false — an
    /// unanswered question must never come back looking like permission to write a format nothing can read.
    /// </remarks>
    private static bool ReadsCurrent((int Major, int Minor, int Build) version) =>
        version.Major > 10 || (version.Major == 10 && version.Build >= MinimumBuildForCurrent);

    /// <summary>
    /// Asks the machine. A call that fails is reported as zero rather than as a version.
    /// </summary>
    private static (int Major, int Minor, int Build) ReadVersion()
    {
        Interlocked.Increment(ref _machineQuestions);

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
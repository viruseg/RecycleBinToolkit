using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace RecycleBinToolkit;

/// <summary>What kind of storage a volume sits on, as far as anything can tell.</summary>
internal enum VolumeStorageKind
{
    /// <summary>
    /// Nobody answered. The driver does not report the property, the volume could not be opened, or it is
    /// not a local volume at all.
    /// </summary>
    Unknown,

    /// <summary>A spinning disk: concurrent requests make its head seek between them.</summary>
    Mechanical,

    /// <summary>A solid-state volume, where concurrent requests are what the controller wants.</summary>
    SolidState,
}

/// <summary>
/// Asks a volume's driver whether seeking costs it anything, and turns the answer into how many threads
/// may read that volume's Recycle Bin at once.
/// </summary>
/// <remarks>
/// <c>IOCTL_STORAGE_QUERY_PROPERTY</c> with <c>StorageDeviceSeekPenaltyProperty</c> is the question
/// Windows itself asks — the built-in defragmenter asks it to decide what to do with a disk. It costs under
/// a tenth of a millisecond, needs no administrator rights and creates no apartment.
/// <para>
/// The answer is a property of the driver, not of the file system, so it is asked once per volume root and
/// remembered for the life of the process. A volume's hardware does not change under a run, and a run that
/// asked twice per entry would pay a device control for every one of ninety thousand entries.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
internal static class VolumeSeekPenalty
{
    /// <summary>
    /// No sharing at all with another handle to the volume. Every driver accepts this; a share request the
    /// driver refuses would make the answer about the share request rather than about the seek penalty.
    /// </summary>
    private const FILE_SHARE_MODE ShareMode = FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE;

    /// <summary>
    /// Asking the volume only. Nothing more is needed to send it a device control, and asking for more would
    /// make the answer fail on a volume this process is allowed to read but not to open for writing.
    /// </summary>
    private const uint DesiredAccess = 0;

    /// <summary>
    /// How many volumes' answers are remembered. A machine has tens of volumes, not thousands, so the bound
    /// is never reached in practice — and it exists so a long-lived process handed an unbounded series of
    /// root strings cannot grow this without limit.
    /// </summary>
    private const int CacheLimit = 64;

    /// <summary>
    /// The most threads a solid-state volume's Recycle Bin records are read on. Reading a batch of records is
    /// bound by per-file work the operating system does rather than by bandwidth, and past this many threads
    /// the measurements stopped improving — so a wider machine gains nothing from the extra ones.
    /// </summary>
    internal const int SolidStateReadDegreeCap = 8;

    /// <summary>The most threads a mechanical volume's Recycle Bin records are read on.</summary>
    /// <remarks>
    /// Eight, which is where the cold sweep put it: the curve peaks there and falls away after, because
    /// each request in flight past that is a seek the head cannot interleave. Sixteen was here before that
    /// sweep and was taken from a warm measurement, where it won by seven percent. Cold is what a caller's
    /// first read does, and it pays twenty percent for that seven. The sweep is in AGENTS.md.
    /// </remarks>
    internal const int MechanicalReadDegreeCap = 8;

    /// <summary>The fewest threads a local volume is read on, however the driver answered.</summary>
    /// <remarks>
    /// Four is a floor rather than a claim that fewer would be worse: it is the smallest figure the cold sweep
    /// measured at, where the curve was still improving steeply below it. A wider one should re-measure before
    /// moving it.
    /// </remarks>
    internal const int ReadDegreeFloor = 4;

    private static readonly ConcurrentDictionary<string, VolumeStorageKind> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What kind of storage a volume sits on.
    /// </summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\"</c>.</param>
    /// <returns>The driver's answer, or <see cref="VolumeStorageKind.Unknown"/>.</returns>
    /// <remarks>
    /// A UNC root answers <see cref="VolumeStorageKind.Unknown"/> without asking anything. A share has no
    /// local volume device — <c>\\.\</c> resolves against this machine — and its cost is network latency,
    /// which parallelising makes worse rather than better.
    /// </remarks>
    internal static VolumeStorageKind Classify(string volumeRoot)
    {
        if (Cache.TryGetValue(volumeRoot, out var known))
        {
            return known;
        }

        var answered = RecycleBinVolumeState.IsUncRoot(volumeRoot) ? VolumeStorageKind.Unknown : Query(volumeRoot);

        // Dropped whole rather than evicted one at a time: an answer is a device control, and re-asking one
        // volume to save the memory of another is a worse trade than a rebuild nobody can see.
        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        Cache[volumeRoot] = answered;
        return answered;
    }

    /// <summary>How many threads may read one volume's Recycle Bin at once.</summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\"</c>.</param>
    /// <returns>
    /// One per core for a solid-state volume, two per core for a mechanical one, each capped where a cold
    /// sweep stopped improving, and one for a share.
    /// </returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A mechanical volume peaks at <see cref="MechanicalReadDegreeCap"/> on a cold read and falls away
    /// after it, because each further request in flight is another seek for the head to interleave.</item>
    /// <item>A solid-state volume keeps improving to <see cref="SolidStateReadDegreeCap"/> and is flat after
    /// it, so the two caps differ in why they exist rather than in what they are.</item>
    /// <item>A share is one: its cost is network latency, which multiplying does not reduce.</item>
    /// </list>
    /// The cold sweep these figures come from is in AGENTS.md.
    /// </remarks>
    internal static int DegreeOfParallelism(string volumeRoot)
    {
        var cores = Environment.ProcessorCount;

        var (cap, scaled) = Classify(volumeRoot) switch
        {
            VolumeStorageKind.SolidState => (SolidStateReadDegreeCap, cores),
            VolumeStorageKind.Mechanical => (MechanicalReadDegreeCap, cores * 2),
            _ => (1, 1)
        };

        if (scaled < ReadDegreeFloor)
        {
            return cap < ReadDegreeFloor ? cap : ReadDegreeFloor;
        }

        return scaled > cap ? cap : scaled;
    }

    private static unsafe VolumeStorageKind Query(string volumeRoot)
    {
        var devicePath = @"\\.\" + volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        using var volume = PInvoke.CreateFile(
            devicePath,
            DesiredAccess,
            ShareMode,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
            null);

        if (volume is null || volume.IsInvalid)
        {
            return VolumeStorageKind.Unknown;
        }

        // The query is a header plus however many parameters the property needs. This one needs none, and the
        // structure is sized by the projection rather than guessed at, because its trailing field is a
        // variable-length one whose size decides what the driver is being told.
        var query = default(STORAGE_PROPERTY_QUERY);
        query.PropertyId = STORAGE_PROPERTY_ID.StorageDeviceSeekPenaltyProperty;
        query.QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery;

        var descriptor = default(DEVICE_SEEK_PENALTY_DESCRIPTOR);

        if (!PInvoke.DeviceIoControl(
                new HANDLE(volume.DangerousGetHandle()),
                PInvoke.IOCTL_STORAGE_QUERY_PROPERTY,
                &query,
                (uint)STORAGE_PROPERTY_QUERY.SizeOf(0),
                &descriptor,
                (uint)sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR),
                null,
                null))
        {
            return VolumeStorageKind.Unknown;
        }

        return descriptor.IncursSeekPenalty ? VolumeStorageKind.Mechanical : VolumeStorageKind.SolidState;
    }
}
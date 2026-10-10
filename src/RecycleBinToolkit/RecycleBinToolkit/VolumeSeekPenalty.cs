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
[SupportedOSPlatform("windows6.0.6000")]
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

    private static readonly ConcurrentDictionary<string, VolumeStorageKind> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What kind of storage a volume sits on.
    /// </summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\\"</c>.</param>
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

    /// <summary>
    /// How many threads may work on a volume's Recycle Bin at once.
    /// </summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\\"</c>.</param>
    /// <returns>
    /// Every core for a solid-state volume, and one for anything else.
    /// </returns>
    /// <remarks>
    /// One is the answer for a mechanical disk because the alternative is not slower, it is harmful: a head
    /// that is asked for four hundred files at once thrashes between them and turns a hundred and eighty
    /// operations per second into five or fifteen. It is the answer for an unknown volume too, because the
    /// two guesses are not symmetric — guessing "solid-state" when the drive is mechanical costs a
    /// thrashing disk, and guessing "mechanical" when it is not costs a little concurrency nobody was
    /// promised.
    /// </remarks>
    internal static int DegreeOfParallelism(string volumeRoot) =>
        Classify(volumeRoot) == VolumeStorageKind.SolidState
            ? Environment.ProcessorCount
            : 1;

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
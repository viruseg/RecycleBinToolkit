using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace RecycleBinToolkit;

/// <summary>The physical disk a volume is a partition of, as that disk's driver names it.</summary>
/// <param name="DeviceType">
/// The kind of device the number belongs to. Carried in the key because the number is only a number within
/// a kind: disk zero of one bus is not disk zero of another.
/// </param>
/// <param name="DeviceNumber">The disk's number on that bus — the one Disk Management prints beside it.</param>
/// <remarks>
/// Two volumes key the same when their drivers named the same device, which is the whole of what grouping
/// by disk needs: work spread over two keys touches two devices, and work inside one key contends for one
/// head.
/// <para>
/// The partition number arrives in the same answer and is deliberately left out. Two partitions of one disk
/// are the case this exists for, and a key carrying the partition would put every volume in a group of its
/// own — the same answer a machine with one disk per volume already gives, having nothing to group.
/// </para>
/// <para>
/// This is what the driver reports, which is not always one physical disk. A volume spanning or striping
/// several is named after the storage it is a volume of, so such volumes may key alike while sharing more
/// than one head. That is the driver's account of the device rather than a claim about its internals, and
/// it is the only account available without taking the volume apart.
/// </para>
/// </remarks>
internal readonly record struct PhysicalDiskKey(int DeviceType, int DeviceNumber);

/// <summary>
/// Asks a volume's driver which physical disk the volume is a partition of, and remembers the answer.
/// </summary>
/// <remarks>
/// <c>IOCTL_STORAGE_GET_DEVICE_NUMBER</c> is the question Windows itself asks — Disk Management asks it to
/// print a volume's disk number — and it costs about as much as <see cref="VolumeSeekPenalty"/>'s does. It
/// needs no administrator rights and creates no apartment.
/// <para>
/// The answer is a property of the hardware, not of the file system, so it is asked once per volume root and
/// remembered for the life of the process. A disk is not repartitioned under a run, and a caller that asked
/// per item would pay a device control for every one of ninety thousand entries.
/// </para>
/// <para>
/// What a root answered <em>not</em> is remembered the same way, because a root that names no disk is the
/// one that would cost most to ask about again: a share and a volume whose driver declines both take a
/// handle and a device control to reach the same refusal, and neither refusal changes under a run.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class PhysicalVolumeDisk
{
    /// <summary>
    /// No sharing at all with another handle to the volume. Every driver accepts this; a share request the
    /// driver refuses would make the answer about the share request rather than about the disk.
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
    /// What each root was last answered, <see langword="null"/> for one that named no disk. Every outcome is
    /// kept, the refusals included: a root that never answers would otherwise be asked again on every call,
    /// and each asking is a handle opened and a device control sent.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PhysicalDiskKey?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which physical disk a volume is a partition of.
    /// </summary>
    /// <param name="volumeRoot">Volume root, such as <c>"X:\"</c>.</param>
    /// <param name="key">
    /// The volume's disk, or <c>default</c> when this returns <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the volume's driver named a disk; <see langword="false"/> when it did not.
    /// </returns>
    /// <remarks>
    /// A UNC root answers <see langword="false"/> without asking anything. A share has no local volume
    /// device — <c>\\.\</c> resolves against this machine — and no physical disk behind it to name.
    /// <para>
    /// A volume whose driver declines answers <see langword="false"/> too, as does a root that opens no
    /// volume at all. Neither raises: what this decides is how much of a machine may work at once, so a
    /// volume that could not say is one a caller groups on its own rather than a failed operation.
    /// </para>
    /// <para>
    /// A refusal is remembered as well as an answer, so a second call about a root that will not answer
    /// costs nothing. That matters most for the volumes a machine has but cannot describe — a share asked
    /// once per item would be a question ninety thousand times over for an answer already known.
    /// </para>
    /// </remarks>
    internal static bool TryIdentify(string volumeRoot, out PhysicalDiskKey key)
    {
        if (Cache.TryGetValue(volumeRoot, out var remembered))
        {
            key = remembered.GetValueOrDefault();
            return remembered.HasValue;
        }

        PhysicalDiskKey? answered = null;

        if (!RecycleBinVolumeState.IsUncRoot(volumeRoot) && Query(volumeRoot, out var found))
        {
            answered = found;
        }

        // Dropped whole rather than evicted one at a time: an answer is a device control, and re-asking one
        // volume to save the memory of another is a worse trade than a rebuild nobody can see. Two threads
        // racing on one root may each ask it, which costs one control apiece and leaves one answer behind —
        // and both were handed the disk they asked about, so neither can be holding a worse one.
        if (Cache.Count >= CacheLimit)
        {
            Cache.Clear();
        }

        Cache[volumeRoot] = answered;

        key = answered.GetValueOrDefault();
        return answered.HasValue;
    }

    private static unsafe bool Query(string volumeRoot, out PhysicalDiskKey key)
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
            key = default;
            return false;
        }

        // The question carries nothing in and one fixed layout out. The count comes back and is checked, because a
        // driver that reports success having written less than it promised has answered nothing at all:
        // `number` is left as it was declared, and (0, 0) is no volume's disk, so taking it would hand a
        // caller a key that is unlike every other volume's — one that groups alone, and looks like an answer.
        var number = default(STORAGE_DEVICE_NUMBER);

        var filled = PInvoke.DeviceIoControl(
            volume,
            PInvoke.IOCTL_STORAGE_GET_DEVICE_NUMBER,
            null,
            new Span<byte>(&number, sizeof(STORAGE_DEVICE_NUMBER)),
            out var written,
            null);

        if (!filled || written != sizeof(STORAGE_DEVICE_NUMBER))
        {
            key = default;
            return false;
        }

        key = new PhysicalDiskKey((int)number.DeviceType, (int)number.DeviceNumber);
        return true;
    }
}
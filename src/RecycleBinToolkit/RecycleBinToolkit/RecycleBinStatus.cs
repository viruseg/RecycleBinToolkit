using System.Runtime.Versioning;

namespace RecycleBinToolkit;

/// <summary>Whether the Recycle Bin is in use for one volume.</summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class RecycleBinStatus
{
    internal RecycleBinStatus(
        string volumeRoot,
        DriveType driveType,
        RecycleBinAvailabilityReason reason)
    {
        VolumeRoot = volumeRoot;
        DriveType = driveType;
        Reason = reason;
    }

    /// <summary>The volume root this answer is about, always with a trailing separator.</summary>
    public string VolumeRoot { get; }

    /// <summary>
    /// The kind of volume that was inspected. <see cref="DriveType.Unknown"/> when the type could not be read.
    /// </summary>
    public DriveType DriveType { get; }

    /// <summary>
    /// Why the Recycle Bin is or is not in use. See <see cref="RecycleBinAvailabilityReason"/> for what each
    /// value means and whether the caller can change it.
    /// </summary>
    public RecycleBinAvailabilityReason Reason { get; }

    /// <summary>
    /// <see langword="true"/> only when <see cref="Reason"/> is
    /// <see cref="RecycleBinAvailabilityReason.Enabled"/>. An answer that could not be established is reported
    /// as <see langword="false"/>.
    /// </summary>
    public bool IsEnabled => Reason == RecycleBinAvailabilityReason.Enabled;
}

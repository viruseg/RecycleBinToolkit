using Windows.Win32.Foundation;

namespace RecycleBinToolkit;

/// <summary>
/// The property keys the Recycle Bin exposes about its entries.
/// </summary>
/// <remarks>
/// Property keys are spelled out here rather than taken from the Shell metadata because they are
/// the one part of this enumeration that is documented as a stable contract: the format IDs and
/// property IDs come from the Windows SDK and do not move between releases or locales. The
/// alternative, the Explorer's column indexes, are locale-dependent and are exactly what this
/// avoids.
/// <para>
/// Only the two keys the Shell actually answers for a Recycle Bin entry are here. <c>System.Size</c>,
/// <c>System.ItemNameDisplay</c> and <c>System.IsDirectory</c> are all documented for these items and
/// all come back empty, which is why the size is read off the entry's own file and the name off the
/// item's display name instead. Declaring them would only invite someone to use them.
/// </para>
/// </remarks>
internal static class RecycleBinPropertyKeys
{
    /// <summary>
    /// <c>System.Recycle.DeletedFrom</c> — the full path the item was deleted from, read only as the
    /// fallback for a display name that is not already a full path.
    /// </summary>
    internal static readonly PROPERTYKEY RecycledFrom = new()
    {
        fmtid = new Guid("9B174B33-40FF-11D2-A27E-00C04FC30871"),
        pid = 2
    };

    /// <summary>
    /// <c>System.Recycle.DateDeleted</c> — when the item was deleted.
    /// </summary>
    internal static readonly PROPERTYKEY DateDeleted = new()
    {
        fmtid = new Guid("9B174B33-40FF-11D2-A27E-00C04FC30871"),
        pid = 3
    };
}

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
/// </remarks>
internal static class RecycleBinPropertyKeys
{
    /// <summary>
    /// <c>System.Recycle.DeletedFrom</c> — the full path the item was deleted from.
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

    /// <summary>
    /// <c>System.ItemNameDisplay</c> — the name the entry had before deletion.
    /// </summary>
    internal static readonly PROPERTYKEY ItemNameDisplay = new()
    {
        fmtid = new Guid("B7F14566-8542-4CBA-AE7F-616DDAF8B49D"),
        pid = 10
    };

    /// <summary>
    /// <c>System.Size</c> — the item's size in bytes.
    /// </summary>
    internal static readonly PROPERTYKEY Size = new()
    {
        fmtid = new Guid("9B5B3E9A-5627-4E21-A16B-0A10800513E0"),
        pid = 12
    };

    /// <summary>
    /// <c>System.IsDirectory</c> — whether the entry is a directory.
    /// </summary>
    internal static readonly PROPERTYKEY IsDirectory = new()
    {
        fmtid = new Guid("C0FA4727-69CA-11D2-B399-00A0C902F648"),
        pid = 14
    };
}

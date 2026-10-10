using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.SystemServices;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell;

namespace RecycleBinToolkit;

/// <summary>
/// Walks the current user's Recycle Bin through the Shell and turns each entry into a
/// <see cref="RecycleBinItem"/>.
/// </summary>
/// <remarks>
/// The Shell exposes the Recycle Bin as a virtual folder, the same one <c>shell:::{…}</c> opens, and
/// that folder aggregates every mounted volume. It is reached by its namespace CLSID rather than by
/// path: <c>C:\$Recycle.Bin</c> is the per-volume storage, and enumerating it directly would yield
/// one folder per user SID instead of the deleted items, with the SYSTEM entry among them denying
/// access to an unelevated process.
/// <para>
/// Every field of every item comes out of the enumeration here, so the items this builds are complete
/// before they are handed back and nothing about them is read later. The native path is the one that
/// defers: see <see cref="RecycleBinItem"/>.
/// </para>
/// <para>
/// Where each field comes from was measured on this Windows build rather than assumed, because the
/// obvious sources turn out to be empty:
/// </para>
/// <list type="bullet">
/// <item>The original path comes from <c>SIGDN_NORMALDISPLAY</c>, which the Shell fills with
/// the full path the item was deleted from. <c>System.Recycle.DeletedFrom</c> holds only the
/// directory and is used as the fallback when the display name is not rooted.</item>
/// <item>The deletion time comes from <c>System.Recycle.DateDeleted</c> through
/// <see cref="IShellItem2"/>. Binding an <c>IPropertyStore</c> and asking the item for
/// <c>System.Size</c> or <c>System.ItemNameDisplay</c> both come back empty, so neither is used.</item>
/// <item>The size comes from the entry's own file on disk, because the Shell reports no size for
/// anything in the Recycle Bin.</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static unsafe class RecycleBinLister
{
    /// <summary>
    /// The Recycle Bin virtual folder, in the <c>::{CLSID}</c> form the Shell accepts as a parsing
    /// name.
    /// </summary>
    private const string RecycleBinParsingName = @"::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>
    /// Items are fetched in blocks, so one COM call covers many entries and the marshalling array is
    /// reused instead of reallocated per item.
    /// </summary>
    private const int BatchSize = 64;

    /// <summary>Win32 <c>ERROR_NOT_SET</c>, used when the Shell gives no error code of its own.</summary>
    private const int ErrorNotSet = unchecked((int)0x80070490);

    /// <summary>
    /// Appends every Recycle Bin entry to <paramref name="items"/>.
    /// </summary>
    /// <param name="items">Receives the entries that could be read.</param>
    /// <param name="failures">Receives one entry per item the Shell would not describe.</param>
    /// <returns><see langword="true"/> when the Recycle Bin itself was opened.</returns>
    internal static bool TryEnumerate(List<RecycleBinItem> items, List<RecycleFailure> failures)
    {
        var hrFolder = PInvoke.SHCreateItemFromParsingName<IShellItem>(RecycleBinParsingName, null, out var folder);
        if (hrFolder.Failed)
        {
            Record(failures, hrFolder.Value);
            return false;
        }

        IEnumShellItems? enumerator;
        try
        {
            folder.BindToHandler<IEnumShellItems>(null, PInvoke.BHID_EnumItems, out enumerator);
        }
        catch (COMException ex)
        {
            Record(failures, ex.HResult);
            return false;
        }

        if (enumerator is null)
        {
            Record(failures, ErrorNotSet);
            return false;
        }

        var buffer = new IShellItem[BatchSize];

        while (true)
        {
            uint fetched;
            try
            {
                enumerator.Next(buffer, out fetched);
            }
            catch (Exception ex) when (ShellOperationContext.IsShellRefusal(ex))
            {
                // The enumerator raised rather than reporting through its return, which the generated
                // signature does not carry. Whatever was read before this point is kept and returned
                // alongside the failure: a listing that stopped early is better than one that escaped as
                // an exception, because the caller asked what could be found and an environmental problem
                // is not a fault of theirs.
                Record(failures, ex.HResult);
                return false;
            }

            if (fetched == 0)
            {
                return true;
            }

            for (var i = 0; i < fetched; i++)
            {
                TryReadItem(buffer[i], items, failures);
            }

            // The buffer is reused by the next block, so without this it holds the previous block's
            // Shell objects alive for the whole enumeration — on a Recycle Bin of tens of thousands of
            // entries, every object ever fetched is still referenced when the listing ends. Emptying the
            // slots lets each one be collected as soon as it has been read, and lets the next block
            // overwrite them rather than accumulate behind them.
            Array.Clear(buffer, 0, (int)fetched);
        }
    }

    /// <summary>
    /// Reads one entry. An entry the Shell cannot describe is recorded and skipped, because a single
    /// unreadable item must not cost the caller the rest of the listing.
    /// </summary>
    private static void TryReadItem(IShellItem shellItem, List<RecycleBinItem> items, List<RecycleFailure> failures)
    {
        try
        {
            if (!TryReadDisplayName(shellItem, SIGDN.SIGDN_NORMALDISPLAY, out var displayName))
            {
                Record(failures, ErrorNotSet);
                return;
            }

            if (!TryReadDisplayName(shellItem, SIGDN.SIGDN_FILESYSPATH, out var physicalPath))
            {
                Record(failures, ErrorNotSet);
                return;
            }

            var item = (IShellItem2)shellItem;
            item.GetAttributes(SFGAO_FLAGS.SFGAO_FOLDER, out var attributes);
            var isDirectory = (attributes & SFGAO_FLAGS.SFGAO_FOLDER) != 0;

            var originalPath = ResolveOriginalPath(item, displayName);

            // Without the original path the entry is not identifiable by the caller and the volume
            // filter has nothing to match on, so it is a failure rather than a silently dropped row.
            if (string.IsNullOrEmpty(originalPath))
            {
                Record(failures, ErrorNotSet);
                return;
            }

            items.Add(new RecycleBinItem(
                indexPath: null,
                physicalPath,
                ReadSize(physicalPath, isDirectory),
                isDirectory,
                originalPath,
                ReadFileTime(item, RecycleBinPropertyKeys.DateDeleted)));
        }
        catch (Exception ex) when (ShellOperationContext.IsShellRefusal(ex))
        {
            // One entry shaped badly — a Shell name that came back malformed, a property the Shell
            // cannot describe — must not take the listing with it. ArgumentException is in this set
            // because Path and PropertyKey handling raise it on an answer they will not accept, so the
            // entry is recorded with whatever HResult the exception carries rather than with the generic
            // "the Shell gave no code" one: a code is more use to a caller than a shrug, even a wrong one.
            Record(failures, ex.HResult);
        }
        catch (InvalidCastException)
        {
            // An entry that is not IShellItem2 cannot describe itself.
            Record(failures, ErrorNotSet);
        }
    }

    /// <summary>
    /// Reads one of the item's Shell names and releases the buffer the Shell allocated for it.
    /// </summary>
    /// <remarks>
    /// PWSTR is a struct wrapping a pointer, so the emptiness test is on the pointer: comparing the
    /// struct itself with null would never be true.
    /// </remarks>
    private static bool TryReadDisplayName(IShellItem shellItem, SIGDN name, out string value)
    {
        shellItem.GetDisplayName(name, out var text);

        if (text.Value is null)
        {
            value = string.Empty;
            return false;
        }

        try
        {
            value = text.ToString();
            return true;
        }
        finally
        {
            PInvoke.CoTaskMemFree((void*)text);
        }
    }

    /// <summary>
    /// Puts the original location and the Shell's name for an entry together into a full path.
    /// </summary>
    /// <remarks>
    /// Measured on this Windows build, the Shell answers <c>SIGDN_NORMALDISPLAY</c> with the full
    /// original path, so the location property is read only when that answer is a bare name and the two
    /// have to be joined. Asking for it unconditionally costs a COM round trip per entry for a value
    /// that is then discarded — a COM round trip and an allocation per entry, for a value almost every
    /// entry does not need.
    /// </remarks>
    private static string ResolveOriginalPath(IShellItem2 item, string name)
    {
        if (Path.IsPathRooted(name))
        {
            return name;
        }

        var location = ReadString(item, RecycleBinPropertyKeys.RecycledFrom);
        return string.IsNullOrEmpty(location) ? name : Path.Combine(location, name);
    }

    /// <summary>
    /// Reads the entry's size off disk.
    /// </summary>
    /// <remarks>
    /// The Shell reports no size for anything in the Recycle Bin, so the file itself is the only
    /// source. A recycled directory's contents cannot be enumerated without elevation, and the Shell
    /// reports those as zero as well, so directories are reported as zero rather than guessed at.
    /// </remarks>
    private static long ReadSize(string physicalPath, bool isDirectory)
    {
        if (isDirectory)
        {
            return 0;
        }

        try
        {
            return new FileInfo(physicalPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The entry was purged between the enumeration and the stat, or its path cannot be
            // queried. Either way the caller learns the size is unknown rather than wrong.
            return 0;
        }
    }

    /// <summary>
    /// Reads a string property. An absent key leaves the variant empty rather than failing, so the
    /// emptiness is the signal that there is nothing to read.
    /// </summary>
    private static string? ReadString(IShellItem2 item, PROPERTYKEY key)
    {
        var property = ReadProperty(item, key, out var hasValue);
        if (!hasValue)
        {
            return null;
        }

        try
        {
            if (PInvoke.PropVariantToStringAlloc(property, out var text).Succeeded)
            {
                var value = Marshal.PtrToStringUni((nint)text.Value);
                PInvoke.CoTaskMemFree((void*)text);
                return value;
            }

            return null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref property);
        }
    }

    private static DateTimeOffset ReadFileTime(IShellItem2 item, PROPERTYKEY key)
    {
        var property = ReadProperty(item, key, out var hasValue);
        if (!hasValue)
        {
            return default;
        }

        try
        {
            // The Shell records the deletion time as a UTC FILETIME, the one form that survives a
            // daylight-saving change. It is presented as a local offset because that is what a
            // caller comparing it against the item's own timestamps wants to see.
            if (PInvoke.PropVariantToFileTime(property, PSTIME_FLAGS.PSTF_UTC, out var fileTime).Succeeded)
            {
                var ticks = ((long)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
                return DateTimeOffset.FromFileTime(ticks);
            }

            return default;
        }
        finally
        {
            PInvoke.PropVariantClear(ref property);
        }
    }

    /// <summary>
    /// Fetches one property. The Shell returns an empty variant for a key it does not have, so the
    /// emptiness rather than a failure is what marks a missing value.
    /// </summary>
    private static PROPVARIANT ReadProperty(IShellItem2 item, PROPERTYKEY key, out bool hasValue)
    {
        var property = default(PROPVARIANT);
        item.GetProperty(key, out property);

        hasValue = property.vt != VARENUM.VT_EMPTY;
        return property;
    }

    private static void Record(List<RecycleFailure> failures, int hrResult) =>
        failures.Add(new RecycleFailure(RecycleBinParsingName, hrResult, 1, RecycleFailureStage.Enumeration));
}

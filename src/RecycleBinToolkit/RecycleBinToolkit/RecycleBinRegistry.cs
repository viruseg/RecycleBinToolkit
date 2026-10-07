using System.Runtime.InteropServices;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the Recycle Bin settings Windows keeps in the registry.
/// </summary>
/// <remarks>
/// <c>RegGetValueW</c> is declared here rather than taken from the Shell metadata because the
/// settings live under the predefined <c>HKEY_CURRENT_USER</c> and <c>HKEY_LOCAL_MACHINE</c> handles.
/// Those are pseudo-handles, not real ones: routing them through a safe handle would add-ref and
/// release handles that were never opened, so the raw integer handle is passed as the native call
/// expects.
/// </remarks>
internal static partial class RecycleBinRegistry
{
    private const uint Success = 0;

    /// <summary>
    /// Accept a value of any type, so a setting stored as a string is still seen.
    /// </summary>
    /// <remarks>
    /// Win32 <c>RRF_RT_ANY</c>. The type mask is not optional: <c>RegGetValueW</c> rejects any value
    /// outside the <c>RRF_RT_*</c> set with <c>ERROR_INVALID_PARAMETER</c>, so a zero here does not
    /// mean "any type", it means the read never succeeds.
    /// </remarks>
    private const uint AnyType = 0x0000FFFF;

    private const uint RegDword = 4;

    private const uint RegSz = 1;

    /// <summary>
    /// Enough for the longest value this reader accepts: a <c>REG_SZ</c> of <c>"true"</c> is eight
    /// characters plus a UTF-16 terminator.
    /// </summary>
    private const int MaxStringBytes = 10;

    private const int CurrentUser = unchecked((int)0x80000001);
    private const int LocalMachine = unchecked((int)0x80000002);

    private const string PolicySubKey = @"Software\Policies\Microsoft\Windows\Explorer";
    private const string BitBucketSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";

    /// <summary>
    /// Reads a registry value as a truthy flag. A missing value and an unreadable one both report
    /// <see langword="false"/>, because neither switches anything off.
    /// </summary>
    /// <param name="rootKey">A predefined HKEY value.</param>
    /// <param name="subKey">Sub key path below that root.</param>
    /// <param name="valueName">Value name.</param>
    /// <returns><see langword="true"/> only when the value is present and reads as 1 or true.</returns>
    internal static bool IsFlagSet(int rootKey, string subKey, string valueName) =>
        TryReadFlag(rootKey, subKey, valueName, out var value) && value;

    /// <summary>
    /// Reads a registry value that Windows may store either as a number or as text.
    /// </summary>
    /// <remarks>
    /// The group policy that forbids the Recycle Bin is written as a <c>REG_DWORD</c> by the policy
    /// engine, but hand-edited or older images carry it as a string. Accepting both means a policy that
    /// is in force is not missed because of how its bits were spelled.
    /// </remarks>
    private static bool TryReadFlag(int rootKey, string subKey, string valueName, out bool result)
    {
        result = false;

        var type = default(uint);
        Span<byte> data = stackalloc byte[MaxStringBytes];
        var size = (uint)MaxStringBytes;

        var status = RegGetValue(rootKey, subKey, valueName, AnyType, out type, data, ref size);
        if (status != Success)
        {
            return false;
        }

        // REG_DWORD, little endian on every platform Windows runs on. A value too large for the buffer
        // comes back as ERROR_MORE_DATA above rather than reaching here, so size is the width of what
        // was actually copied and is what decides the shape.
        if (type == RegDword && size == sizeof(int))
        {
            result = data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 0;
            return true;
        }

        // REG_SZ, "1" or "true" in UTF-16 with a terminator.
        if (type == RegSz && size <= MaxStringBytes)
        {
            var text = data[..(int)size];
            result = IsWideText(text, "1") || IsWideText(text, "true");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a UTF-16LE buffer holds exactly <paramref name="ascii"/>, terminator included.
    /// </summary>
    private static bool IsWideText(ReadOnlySpan<byte> wide, string ascii)
    {
        if (wide.Length != (ascii.Length + 1) * 2)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (wide[i * 2] != (byte)ascii[i] || wide[(i * 2) + 1] != 0)
            {
                return false;
            }
        }

        return wide[^2] == 0 && wide[^1] == 0;
    }

    /// <summary>
    /// Whether an administrator has forbidden the Recycle Bin, for this user or for the machine.
    /// </summary>
    internal static bool IsDisabledByPolicy() =>
        IsFlagSet(CurrentUser, PolicySubKey, "NoRecycleFiles") ||
        IsFlagSet(LocalMachine, PolicySubKey, "NoRecycleFiles");

    /// <summary>
    /// Whether this volume is set to delete immediately instead of recycling.
    /// </summary>
    /// <remarks>
    /// The per-volume state is keyed on the volume's GUID path, not on its drive letter, which is why a
    /// volume mounted at more than one letter shares one setting.
    /// </remarks>
    internal static bool IsNukeOnDelete(string volumeGuidPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeGuidPath);

        return IsFlagSet(CurrentUser, $@"{BitBucketSubKey}\Volume\{volumeGuidPath}", "NukeOnDelete");
    }

    /// <summary>
    /// <c>RegGetValueW</c>, with its parameters in the order the native function takes them.
    /// </summary>
    /// <remarks>
    /// <c>pvData</c> is the buffer and <c>pcbData</c> the size, in that order. Declared the other way
    /// round the call still returns, but the native side writes the value's byte count into the caller's
    /// buffer and reads the buffer address back as the available size, so every read reports
    /// <c>ERROR_MORE_DATA</c> against a zeroed stack buffer and the caller sees nothing.
    /// </remarks>
    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValue(
        int rootKey,
        string subKey,
        string valueName,
        uint flags,
        out uint valueType,
        Span<byte> data,
        ref uint dataSize);
}

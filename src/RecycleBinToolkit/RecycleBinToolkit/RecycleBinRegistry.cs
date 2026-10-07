using System.Runtime.InteropServices;

namespace RecycleBinToolkit;

/// <summary>
/// Reads the Recycle Bin settings Windows keeps in the registry.
/// </summary>
/// <remarks>
/// <see cref="RegGetValueW"/> is declared here rather than taken from the Shell metadata because the
/// settings live under the predefined <c>HKEY_CURRENT_USER</c> and <c>HKEY_LOCAL_MACHINE</c> handles.
/// Those are pseudo-handles, not real ones: routing them through a safe handle would add-ref and
/// release handles that were never opened, so the raw integer handle is passed as the native call
/// expects.
/// </remarks>
internal static partial class RecycleBinRegistry
{
    private const uint Success = 0;

    /// <summary>Accept a value of any type, so a setting stored as a string is still seen.</summary>
    private const uint AnyType = 0x00000000;

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
        var size = 4u;
        Span<byte> data = stackalloc byte[4];

        var status = RegGetValue(rootKey, subKey, valueName, AnyType, out type, ref size, data);
        if (status != Success)
        {
            return false;
        }

        // REG_DWORD, little endian on every platform Windows runs on.
        if (type == 4 && size == 4)
        {
            result = data[0] != 0 || data[1] != 0 || data[2] != 0 || data[3] != 0;
            return true;
        }

        // REG_SZ, "1" or "true" in UTF-16, which is 4 or 10 bytes with the terminator.
        if (type == 1 && size is 4 or 10)
        {
            result = data[0] == (byte)'1' ||
                     (data[0] == (byte)'t' && data[1] == (byte)'r' && data[2] == (byte)'u' && data[3] == (byte)'e');
            return true;
        }

        return false;
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

    [LibraryImport("advapi32.dll", EntryPoint = "RegGetValueW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegGetValue(
        int rootKey,
        string subKey,
        string valueName,
        uint flags,
        out uint valueType,
        ref uint dataSize,
        Span<byte> data);
}

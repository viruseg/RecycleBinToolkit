using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RecycleBinToolkit.Tests.Host;

namespace RecycleBinToolkit.Tests.Infrastructure;

internal static class Junction
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint MountPointTag = 0xA0000003;
    private const int MountPointHeaderLength = 12;

    internal static void Create(string linkPath, string targetPath)
    {
        if (!TryCreate(linkPath, targetPath, out var error))
        {
            if (IsUnsupported(error))
            {
                Check.Skip($"a junction could not be created on this volume (Win32 {error})");
            }

            throw new InvalidOperationException($"Creating the junction {linkPath} failed with Win32 {error}.");
        }
    }

    internal static bool TryCreate(string linkPath, string targetPath, out int error)
    {
        error = 0;

        Directory.CreateDirectory(linkPath);

        using var directory = CreateFile(
            linkPath,
            GenericWrite,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (directory.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        var target = targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // The substitute name is the one the object manager follows, so it carries the device prefix that a
        // drive letter alone does not.
        var substitute = (@"\??\" + target).ToCharArray();
        var print = target.ToCharArray();

        var dataLength = MountPointHeaderLength + ((substitute.Length + 1) + (print.Length + 1)) * 2;
        var buffer = new byte[8 + dataLength];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)(substitute.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)((substitute.Length + 1) * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)(print.Length * 2));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 0);

        Encoding.Unicode.GetBytes(substitute, 0, substitute.Length, buffer, 20);
        Encoding.Unicode.GetBytes(print, 0, print.Length, buffer, 20 + (substitute.Length + 1) * 2);

        if (!DeviceIoControl(directory, SetReparsePoint, buffer, buffer.Length, null, 0, out _, IntPtr.Zero))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        return true;
    }

    private static bool IsUnsupported(int error) => error is 1 or 2 or 50 or 87 or 1314;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inBuffer,
        int inBufferSize,
        byte[]? outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

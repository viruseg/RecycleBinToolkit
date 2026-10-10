using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RecycleBinToolkit;

/// <summary>What one <c>$I</c> record says about the item it describes.</summary>
/// <param name="OriginalPath">The path the item was deleted from, exactly as it was then.</param>
/// <param name="Size">The item's size in bytes when it was deleted, or 0 for a directory.</param>
/// <param name="DeletedAtUtc">When it was deleted.</param>
internal readonly record struct NativeIndexRecord(string OriginalPath, long Size, DateTime DeletedAtUtc);

/// <summary>
/// Reads and writes the <c>$I</c> records that describe a Recycle Bin's contents.
/// </summary>
/// <remarks>
/// Two formats, and the difference is the whole reason this backend is gated on a Windows build. Version 1
/// has a fixed 520-byte path field, which is 260 characters and no more — Windows Vista through 8.1 wrote
/// it, and a path past <c>MAX_PATH</c> could not be described. Version 2 replaced the field with a length
/// and carries a path of any length; Windows 10 build 14393 was the first Shell that reads it.
/// <para>
/// Version 1 is read because a bin can still hold entries written by an older Windows; it is never written,
/// because a Shell too old to read it is a Shell whose Restore button would not work on what this wrote.
/// </para>
/// </remarks>
internal static class NativeIndexFile
{
    /// <summary>The format this backend writes.</summary>
    internal const int Version = 2;

    /// <summary>
    /// Bytes before the path: version, size and deletion time as three 64-bit fields, then the path's
    /// character count as a 32-bit one.
    /// </summary>
    internal const int HeaderLength = 28;

    /// <summary>
    /// Size of the buffer a record is read into on the stack. A path of about 480 characters fits, which is
    /// deeper than the 260 characters the Shell could manage and past the length at which a real caller
    /// stops.
    /// </summary>
    internal const int StackBufferLength = 1024;

    /// <summary>Bytes of a version 1 record: its three fields, then a fixed 520-byte path field.</summary>
    private const int LegacyLength = 544;

    /// <summary>
    /// Bytes before a version 1 path: three 64-bit fields and no character count. Two bytes shorter than
    /// <see cref="HeaderLength"/>, because the count is exactly what version 2 added.
    /// </summary>
    private const int LegacyHeaderLength = 24;

    /// <summary>Bytes of a version 1 path field: 260 UTF-16 characters.</summary>
    private const int LegacyPathLength = 520;

    /// <summary>
    /// The longest path Windows can name, in characters. The extended-length prefix is what buys paths past
    /// <c>MAX_PATH</c> and it stops at 32,767 characters, so a record declaring more is describing something
    /// no caller could have deleted.
    /// </summary>
    /// <remarks>
    /// Checked against the character count rather than against the bytes actually read, which would reject
    /// the long paths the rented read below exists for. It is also what keeps that rent bounded: at this
    /// ceiling the buffer is about 64 KB, against the two gigabytes a bare overflow guard would admit.
    /// </remarks>
    internal const int MaxPathCharacters = 32767;

    /// <summary>
    /// The oldest file time <see cref="DateTime.FromFileTimeUtc"/> will convert: midnight on 1 January 1601,
    /// the epoch the conversion counts from. Below it there is no instant to name.
    /// </summary>
    private const long MinFileTime = 0;

    /// <summary>
    /// The newest one, which is the last instant a <see cref="DateTime"/> can hold. The conversion raises on
    /// anything past it, and this is a number read out of a file rather than one a caller produced.
    /// </summary>
    private static readonly long MaxFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// Writes a record describing a deletion.
    /// </summary>
    /// <param name="indexPath">Where to write it. Must not already exist.</param>
    /// <param name="originalPath">
    /// The path the item is being deleted from, in the spelling Windows uses — no <c>\\?\</c> prefix, which
    /// is a .NET convenience the Shell has never heard of.
    /// </param>
    /// <param name="size">The item's size in bytes, or 0 for a directory.</param>
    /// <param name="deletionUtc">When the deletion happened.</param>
    /// <remarks>
    /// The record is created new rather than overwritten, because overwriting a name another entry holds
    /// would destroy that entry's record and leave two payloads under one name.
    /// <para>
    /// It is not written through. One flush per item is one disk flush per item, which on a mechanical
    /// volume is a stall per file; the window that would close is the one the caller orders its steps
    /// around — the record exists before the payload moves, so a crash between the two leaves the caller's
    /// file where it was and an index with no payload, which the next empty collects.
    /// </para>
    /// </remarks>
    internal static void Write(string indexPath, string originalPath, long size, DateTime deletionUtc)
    {
        var characters = originalPath.Length + 1;
        var total = HeaderLength + (characters * 2);

        using var handle = File.OpenHandle(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        Span<byte> buffer = total <= StackBufferLength ? stackalloc byte[StackBufferLength] : new byte[total];

        BinaryPrimitives.WriteInt64LittleEndian(buffer, Version);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], size);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[16..], deletionUtc.ToFileTimeUtc());
        BinaryPrimitives.WriteInt32LittleEndian(buffer[24..], characters);

        Encoding.Unicode.GetBytes(originalPath.AsSpan(), buffer[HeaderLength..]);
        buffer[total - 2] = 0;
        buffer[total - 1] = 0;

        RandomAccess.Write(handle, buffer[..total], 0);
    }

    /// <summary>
    /// Reads a record, or reports that it could not be read.
    /// </summary>
    /// <param name="indexPath">Path of an <c>$I</c> record.</param>
    /// <param name="record">Receives what the record says.</param>
    /// <returns>
    /// <see langword="true"/> when the record was readable and understood. <see langword="false"/> when it
    /// could not be opened, is too short, declares a version this does not know, or describes a path longer
    /// than the file holds.
    /// </returns>
    /// <remarks>
    /// Never throws. A record that cannot be read is one entry a caller cannot be told about, and an
    /// unreadable entry must not cost the caller the other ten thousand — so this answers rather than
    /// faults, and the caller records a failure against it.
    /// <para>
    /// Opened with <see cref="FileShare.ReadWrite"/> because Explorer may be writing beside it, and read
    /// with <see cref="RandomAccess"/> into a stack buffer so a bin of ninety thousand entries costs no
    /// heap at all for the records themselves.
    /// </para>
    /// </remarks>
    internal static bool TryRead(string indexPath, out NativeIndexRecord record) =>
        TryRead(indexPath, out record, out _);

    /// <summary>
    /// Reads a record, saying why it could not be read when the reason is the filesystem's.
    /// </summary>
    /// <param name="indexPath">Path of an <c>$I</c> record.</param>
    /// <param name="record">Receives what the record says.</param>
    /// <param name="hResult">
    /// Receives the code the filesystem raised, or <c>0</c> when the record was readable or simply was not
    /// a record at all.
    /// </param>
    /// <returns><see langword="true"/> when the record was readable and understood.</returns>
    /// <remarks>
    /// The distinction is the caller's to make and not this one's. A record that is malformed says nothing
    /// about the bin — it is not an entry, and reporting it as one a caller could not read would be noise. A
    /// record that could not be <em>opened</em> is an entry somebody else is holding, and the caller listing
    /// a bin needs to hear that one is missing from what they got.
    /// </remarks>
    internal static bool TryRead(string indexPath, out NativeIndexRecord record, out int hResult)
    {
        record = default;
        hResult = 0;

        try
        {
            using var handle = File.OpenHandle(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Span<byte> buffer = stackalloc byte[StackBufferLength];
            var read = RandomAccess.Read(handle, buffer, 0);

            if (read < HeaderLength)
            {
                return false;
            }

            var version = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            var size = BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]);

            // Checked before it is converted rather than around the conversion. Raising here is caught
            // below as an ArgumentException and turned into a failure carrying the runtime's own code, which
            // says a record could not be read — about a file that is not a record at all, and about nothing
            // the bin did. The contract is the other way round: only a record that could not be opened is
            // worth reporting.
            var fileTime = BinaryPrimitives.ReadInt64LittleEndian(buffer[16..]);
            if (fileTime < MinFileTime || fileTime > MaxFileTime)
            {
                return false;
            }

            var deleted = DateTime.FromFileTimeUtc(fileTime);

            if (version == Version)
            {
                var characters = BinaryPrimitives.ReadInt32LittleEndian(buffer[24..]);

                // Guarded before anything is multiplied or sliced, and against the longest path Windows can
                // name rather than against the largest number an `int` holds. A path is at most 32,767
                // characters with the extended-length prefix, so a larger count is not a path this machine
                // could have produced — it is a number in a file, and the bin directory is one any program
                // running as this user can write to. Taking it at its word rents a buffer of whatever size
                // it names, up to the two gigabytes the largest admitted count asks for, and the addition
                // below wraps before that on the counts just under it. Neither is an answer, and both come
                // out of a method documented never to throw: an allocation failure is not among the
                // exceptions caught below.
                if (characters < 1 || characters > MaxPathCharacters)
                {
                    return false;
                }

                var pathBytes = characters * 2;

                if (HeaderLength + pathBytes <= read)
                {
                    record = new NativeIndexRecord(
                        Encoding.Unicode.GetString(buffer.Slice(HeaderLength, pathBytes)).TrimEnd('\0'),
                        size,
                        deleted);

                    return true;
                }

                if (!TryReadRented(handle, pathBytes, size, deleted, out record))
                {
                    return false;
                }

                return true;
            }

            if (version != 1 || read < LegacyLength)
            {
                return false;
            }

            record = new NativeIndexRecord(
                Encoding.Unicode.GetString(buffer.Slice(LegacyHeaderLength, LegacyPathLength)).TrimEnd('\0'),
                size,
                deleted);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Purged between the enumeration and this read, or held open by something else. Either way the
            // caller is told, and told with the code the filesystem gave.
            hResult = ex.HResult;
            return false;
        }
    }

    /// <summary>
    /// Reads the format version a record declares, for a case that wants to state what was written rather
    /// than only read it back.
    /// </summary>
    /// <param name="indexPath">Path of an <c>$I</c> record.</param>
    /// <returns>The version, or 0 when the file could not be opened or is too short to declare one.</returns>
    internal static long ReadVersion(string indexPath)
    {
        try
        {
            using var handle = File.OpenHandle(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            Span<byte> buffer = stackalloc byte[8];
            return RandomAccess.Read(handle, buffer, 0) == 8
                ? BinaryPrimitives.ReadInt64LittleEndian(buffer)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads a path too long for the stack buffer, into exactly as much rented memory as it needs.
    /// </summary>
    /// <remarks>
    /// The rare case is why it is separate rather than why the buffer is large: a path past about 480
    /// characters. Renting the exact size rather than a second fixed buffer means the cost of the case that
    /// does happen is the size of the path and nothing more.
    /// </remarks>
    private static bool TryReadRented(SafeFileHandle handle, int pathBytes, long size, DateTime deleted, out NativeIndexRecord record)
    {
        var rented = ArrayPool<byte>.Shared.Rent(HeaderLength + pathBytes);

        try
        {
            var read = RandomAccess.Read(handle, rented, 0);
            if (read < HeaderLength + pathBytes)
            {
                record = default;
                return false;
            }

            record = new NativeIndexRecord(
                Encoding.Unicode.GetString(rented.AsSpan(HeaderLength, pathBytes)).TrimEnd('\0'),
                size,
                deleted);

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
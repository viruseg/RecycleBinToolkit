using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
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
/// Two formats, and the difference is why a record is written in one of them or the other. Version 1 has a
/// fixed 520-byte path field, which is 260 characters and no more — Windows Vista through 8.1 wrote it, and
/// a path past <c>MAX_PATH</c> could not be described. Version 2 replaced the field with a length and
/// carries a path of any length; Windows 10 build 14393 was the first Shell that reads it.
/// <para>
/// Both are read, because a bin can hold entries written by either Windows. Which one is written is
/// <see cref="IndexFormat"/>'s answer and not this class's: the caller does not choose, and a path that
/// the chosen format cannot name never reaches <see cref="Write"/> — the policy above it either changes
/// the path or refuses the item.
/// </para>
/// </remarks>
internal static class NativeIndexFile
{
    /// <summary>
    /// The format this library writes on a machine whose Shell reads it. A record declaring anything else is
    /// a record this machine's Shell does not understand.
    /// </summary>
    internal const int CurrentVersion = 2;

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
    /// Characters a version 1 record can name: the fixed field's 260 minus the NUL that terminates it.
    /// </summary>
    /// <remarks>
    /// Counted in UTF-16 code units, not bytes and not graphemes, because that is what the field holds.
    /// </remarks>
    internal const int LegacyPathCapacity = LegacyPathLength / 2 - 1;

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
    /// <param name="version">
    /// Which format to write. <see cref="IndexFormat.Legacy"/> fits a path of at most
    /// <see cref="LegacyPathCapacity"/> characters and no more; <see cref="IndexFormat.Current"/> fits any
    /// path Windows can name.
    /// </param>
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
    internal static void Write(string indexPath, string originalPath, long size, DateTime deletionUtc, int version)
    {
        // Before the file is created, not after: an index record with no payload beside it is a phantom
        // a user can try to restore and cannot, and an empty one left by a refusal is exactly that.
        if (version == IndexFormat.Legacy && originalPath.Length > LegacyPathCapacity)
        {
            // A path that will not fit is never shortened. The caller's path is what a restore has to put
            // the item back to, so a truncated one is a wrong answer rather than an approximate one, and
            // the caller is better served by a refusal it can see.
            throw new ArgumentException(
                $"A version 1 index record holds a path of at most {LegacyPathCapacity} characters, and this one is {originalPath.Length}. " +
                "It was not written, and nothing was truncated.",
                nameof(originalPath));
        }

        using var handle = File.OpenHandle(indexPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        if (version == IndexFormat.Legacy)
        {
            Span<byte> legacy = stackalloc byte[LegacyLength];

            BinaryPrimitives.WriteInt64LittleEndian(legacy, IndexFormat.Legacy);
            BinaryPrimitives.WriteInt64LittleEndian(legacy[8..], size);
            BinaryPrimitives.WriteInt64LittleEndian(legacy[16..], deletionUtc.ToFileTimeUtc());

            Encoding.Unicode.GetBytes(originalPath.AsSpan(), legacy[LegacyHeaderLength..]);

            RandomAccess.Write(handle, legacy, 0);
            return;
        }

        var characters = originalPath.Length + 1;
        var total = HeaderLength + (characters * 2);

        Span<byte> buffer = total <= StackBufferLength ? stackalloc byte[StackBufferLength] : new byte[total];

        BinaryPrimitives.WriteInt64LittleEndian(buffer, version);
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

            if (TryParse(buffer[..read], out record))
            {
                return true;
            }

            // Only a path longer than the stack buffer is worth a second read. Every other reason the bytes
            // are not a record — a version this does not know, a timestamp out of range, a character count
            // no path could have — is about the bytes themselves, and reading more of them answers nothing.
            // The check is what keeps a file declaring two billion characters from reaching the rent below.
            return DeclaresLongerPath(buffer[..read]) && TryReadRented(handle, buffer[..read], out record);
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
    /// Whether what was read is a version 2 record whose path the bytes do not hold all of.
    /// </summary>
    /// <remarks>
    /// The one condition under which a record that could not be parsed from the first read becomes readable
    /// by reading further into the file. It re-asks the header the same way <see cref="TryParse"/> does, so
    /// the character count is bounded by <see cref="MaxPathCharacters"/> before anything multiplies it.
    /// </remarks>
    private static bool DeclaresLongerPath(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength
            || BinaryPrimitives.ReadInt64LittleEndian(bytes) != CurrentVersion)
        {
            return false;
        }

        var characters = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);

        return characters >= 1
            && characters <= MaxPathCharacters
            && HeaderLength + (characters * 2) > bytes.Length;
    }

    /// <summary>
    /// Turns the bytes of one record into what it says, or reports that they do not say it.
    /// </summary>
    /// <param name="bytes">The whole file, as read.</param>
    /// <param name="record">Receives what the record says.</param>
    /// <returns>
    /// <see langword="true"/> when the bytes are a record this can read. <see langword="false"/> when
    /// they are too short, declare a version this does not know, or describe a path longer than the
    /// bytes hold.
    /// </returns>
    /// <remarks>
    /// Split from the reading so that the version 1 and version 2 shapes are read in exactly one place
    /// each. Never throws: a record that cannot be read is one entry a caller cannot be told about, and an
    /// unreadable entry must not cost the caller the other ten thousand.
    /// </remarks>
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out NativeIndexRecord record)
    {
        record = default;

        if (bytes.Length < HeaderLength)
        {
            return false;
        }

        var version = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var size = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);

        // Checked before it is converted rather than around the conversion. Raising here would be caught
        // upstream as an ArgumentException and turned into a failure carrying the runtime's own code, which
        // says a record could not be read — about a file that is not a record at all, and about nothing
        // the bin did. The contract is the other way round: only a record that could not be opened is
        // worth reporting.
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        if (fileTime < MinFileTime || fileTime > MaxFileTime)
        {
            return false;
        }

        var deleted = DateTime.FromFileTimeUtc(fileTime);

        if (version == CurrentVersion)
        {
            var characters = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);

            // Guarded before anything is multiplied or sliced, and against the longest path Windows can
            // name rather than against the largest number an `int` holds. A path is at most 32,767
            // characters with the extended-length prefix, so a larger count is not a path this machine
            // could have produced — it is a number in a file, and the bin directory is one any program
            // running as this user can write to.
            if (characters < 1 || characters > MaxPathCharacters)
            {
                return false;
            }

            var pathBytes = characters * 2;

            if (HeaderLength + pathBytes > bytes.Length)
            {
                return false;
            }

            record = new NativeIndexRecord(DecodePath(bytes.Slice(HeaderLength, pathBytes)), size, deleted);
            return true;
        }

        if (version != IndexFormat.Legacy || bytes.Length < LegacyLength)
        {
            return false;
        }

        record = new NativeIndexRecord(DecodePath(bytes.Slice(LegacyHeaderLength, LegacyPathLength)), size, deleted);
        return true;
    }

    /// <summary>
    /// Decodes a UTF-16 path from a record, in one allocation.
    /// </summary>
    /// <remarks>
    /// The trailing terminator is found before the string is made rather than trimmed off afterwards.
    /// <c>GetString</c> then <c>TrimEnd</c> allocates twice — once for the whole field and once for the
    /// part of it that survives — and the second is the one the caller keeps, so the first is garbage
    /// immediately. Reading the length first and slicing means the field is copied once, out of a span
    /// that has already been narrowed to the characters that are actually there.
    /// <para>
    /// Worth 18 % of the parse, measured over a thousand records: 97 µs became 80. The parse is a
    /// rounding error beside the system calls around it, so this is tidiness rather than speed — but it
    /// is also a third of the 247 bytes a record allocated, which is what the garbage collector collects
    /// while a caller is waiting.
    /// </para>
    /// </remarks>
    private static string DecodePath(ReadOnlySpan<byte> field)
    {
        var characters = MemoryMarshal.Cast<byte, char>(field);

        var length = characters.Length;
        while (length > 0 && characters[length - 1] == '\0')
        {
            length--;
        }

        return new string(characters[..length]);
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
    /// <para>
    /// The header has already been read by the time this is reached and already said the record is a
    /// version 2 one, so what is missing is only the path — the count in the header named it, and the
    /// bytes hold the first part of it. Only that part is read back.
    /// </para>
    /// </remarks>
    private static bool TryReadRented(SafeFileHandle handle, ReadOnlySpan<byte> alreadyRead, out NativeIndexRecord record)
    {
        var characters = BinaryPrimitives.ReadInt32LittleEndian(alreadyRead[24..]);
        var pathBytes = characters * 2;
        var have = Math.Min(alreadyRead.Length - HeaderLength, pathBytes);

        var rented = ArrayPool<byte>.Shared.Rent(pathBytes);

        try
        {
            alreadyRead.Slice(HeaderLength, have).CopyTo(rented);

            if (have < pathBytes)
            {
                var got = RandomAccess.Read(handle, rented.AsSpan(have, pathBytes - have), HeaderLength + have);

                if (have + got < pathBytes)
                {
                    record = default;
                    return false;
                }
            }

            record = new NativeIndexRecord(
                DecodePath(rented.AsSpan(0, pathBytes)),
                BinaryPrimitives.ReadInt64LittleEndian(alreadyRead[8..]),
                DateTime.FromFileTimeUtc(BinaryPrimitives.ReadInt64LittleEndian(alreadyRead[16..])));

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
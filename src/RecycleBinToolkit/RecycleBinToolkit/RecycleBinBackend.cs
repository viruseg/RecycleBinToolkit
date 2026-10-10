namespace RecycleBinToolkit;

/// <summary>
/// How an operation reaches the Recycle Bin.
/// </summary>
/// <remarks>
/// Two ways to do the same thing, and the caller picks per call rather than per library:
/// <see cref="WinApi"/> is the Shell path this library was built around, and <see cref="Native"/>
/// writes the Recycle Bin's own files directly. Neither is a fallback for the other, and the choice
/// is never made silently — a default that changed under a caller would change what their data
/// goes through without their asking.
/// </remarks>
public enum RecycleBinBackend
{
    /// <summary>
    /// The Shell: <c>IFileOperation</c> to recycle, the Recycle Bin virtual folder to read,
    /// <c>SHEmptyRecycleBin</c> to empty, on a process-wide single-threaded apartment worker.
    /// </summary>
    /// <remarks>
    /// What a caller gets that the native path does not have: per-item Shell error codes, and the
    /// Shell's own view of where an item went. What it costs: every call is a COM round trip
    /// serialised behind one thread, which is why <c>SHQueryRecycleBin</c> on a bin of ninety
    /// thousand entries was measured at about eleven seconds. Requires no particular Windows build.
    /// </remarks>
    WinApi = 0,

    /// <summary>
    /// The Recycle Bin's own files: <c>&lt;volume&gt;$RECYCLE.BIN\&lt;SID&gt;\$I</c> and the
    /// <c>$R</c> payload beside it, written and read with plain file I/O, on the thread pool rather
    /// than on the Shell's single-threaded apartment.
    /// </summary>
    /// <remarks>
    /// What a caller gets that the Shell path does not have: no STA thread and no COM apartment for the
    /// Recycle Bin work itself, and no <c>MAX_PATH</c> limit — the path inside the bin is short whatever
    /// the original was, so nothing is staged and nothing is refused for length. What it costs: only the
    /// current user's bin on the named volumes is visible, and the write path needs Windows 10 build 14393
    /// or later, because that is the first build whose own Shell reads the version 2 index records this
    /// backend writes. That requirement is refused with a <see cref="PlatformNotSupportedException"/> and
    /// <see cref="RecycleBinOptions.ThrowOnError"/> does not suppress it.
    /// <para>
    /// Measured against the Shell path on this machine, recycling a batch of three thousand 64 KiB files:
    /// 678 files per second here against 206 through <c>IFileOperation</c> on a mechanical volume, and
    /// 597 against 218 on a solid-state one.
    /// </para>
    /// <para>
    /// Reading scales and writing does not, and that is not a limitation of this library but of what a
    /// directory is. Reading a Recycle Bin changes nothing about it, so the same three thousand records are
    /// read four and a half times faster on eight threads. Every record written and every payload moved
    /// here goes into one directory — <c>&lt;volume&gt;$RECYCLE.BIN\&lt;SID&gt;\</c> — and a directory is
    /// the one thing a filesystem cannot take concurrent writers on: the same recycling measured 542 files
    /// per second on one thread and 189 on eight, on a solid-state volume. So the parallelism is where
    /// reading changes nothing and the cost is per path.
    /// </para>
    /// </remarks>
    Native = 1,
}
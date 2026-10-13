namespace RecycleBinToolkit;

/// <summary>How one entry a restore was asked for ended.</summary>
public enum RestoreStatus
{
    /// <summary>The entry is back on disk.</summary>
    Restored = 0,

    /// <summary>The entry is still in the Recycle Bin. <see cref="RestoreProgress.Failure"/> carries why.</summary>
    Failed = 1,

    /// <summary>The entry is still in the Recycle Bin because the conflict policy said to leave it there.</summary>
    Skipped = 2,

    /// <summary>The entry's contents went into a directory that was already at its path.</summary>
    Merged = 3
}

/// <summary>What became of one entry a restore was asked for.</summary>
/// <remarks>
/// One per entry that was passed in, whether or not anything happened to it. <see cref="Elapsed"/> is
/// measured when the report is created, not when it reaches the handler, so a slow handler does not make
/// the next one look slow.
/// </remarks>
/// <param name="Index">Position of this entry in the operation's own numbering, starting at 1.</param>
/// <param name="Count">Number of entries the operation was asked about.</param>
/// <param name="Status">What became of the entry.</param>
/// <param name="Path">Where the entry landed, or the record it came from when it landed nowhere.</param>
/// <param name="Elapsed">Time from the start of the call to the moment this report was created.</param>
/// <param name="Failure">Why it was skipped or failed, or <see langword="null"/> when nothing went wrong.</param>
public sealed record RestoreProgress(
    int Index,
    int Count,
    RestoreStatus Status,
    string Path,
    TimeSpan Elapsed,
    RecycleFailure? Failure);
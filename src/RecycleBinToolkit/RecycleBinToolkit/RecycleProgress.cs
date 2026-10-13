namespace RecycleBinToolkit;

/// <summary>How one item a recycle was asked for ended.</summary>
public enum RecycleStatus
{
    /// <summary>The item is in the Recycle Bin.</summary>
    Recycled = 0,

    /// <summary>The operation was attempted and did not happen. <see cref="RecycleProgress.Failure"/> carries why.</summary>
    Failed = 1,

    /// <summary>The item was not attempted and is still where the caller left it.</summary>
    Refused = 2,

    /// <summary>The item was removed outright rather than handed over to the Recycle Bin.</summary>
    DeletedPermanently = 3
}

/// <summary>What became of one item a recycle was asked for.</summary>
/// <remarks>
/// One per item that was passed in, whether or not anything happened to it, and including a path that was
/// refused, repeated, or never named a file. <see cref="Elapsed"/> is measured when the report is created,
/// not when it reaches the handler, so a slow handler does not make the next one look slow.
/// </remarks>
/// <param name="Index">Position of this item in the operation's own numbering, starting at 1.</param>
/// <param name="Count">Number of items the operation was asked about.</param>
/// <param name="Status">What became of the item.</param>
/// <param name="Path">The path as the caller wrote it. A <see langword="null"/> entry is reported as an empty string.</param>
/// <param name="Elapsed">Time from the start of the call to the moment this report was created.</param>
/// <param name="Failure">Why it was refused or failed, or <see langword="null"/> when nothing went wrong.</param>
public sealed record RecycleProgress(
    int Index,
    int Count,
    RecycleStatus Status,
    string Path,
    TimeSpan Elapsed,
    RecycleFailure? Failure);
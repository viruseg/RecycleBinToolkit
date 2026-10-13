namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// What a case's progress handler has been told so far, read from the case's own thread.
/// </summary>
internal static class Reports
{
    /// <summary>
    /// A copy of what the handler has recorded.
    /// </summary>
    /// <remarks>
    /// The lock is what makes the read a snapshot rather than a race: the handler runs on a thread of its
    /// own unless the case asked to wait for it, and a caller that did not wait has no other way to read
    /// the list at all.
    /// </remarks>
    internal static List<T> SoFar<T>(List<T> collected)
    {
        lock (collected)
        {
            return [.. collected];
        }
    }
}
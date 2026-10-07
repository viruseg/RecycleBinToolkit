using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace RecycleBinToolkit;

/// <summary>
/// Per-item progress sink for a Shell delete operation.
/// </summary>
/// <remarks>
/// This is the only way to learn why an individual file survived: with
/// <c>FOF_NOERRORUI</c> and no sink, <c>PerformOperations</c> returns <c>S_OK</c> while silently
/// skipping failures. Shell callbacks are not guaranteed to arrive in queue order, so an item is
/// matched by the path the Shell reports for it; queue position is only a fallback.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed unsafe class RecycleProgressSink : IFileOperationProgressSink
{
    /// <summary>Sentinel stored for a path the Shell has not reported on yet.</summary>
    /// <remarks>
    /// Deliberately <see cref="int.MinValue"/> rather than zero: the Shell reports a successful
    /// delete as <c>0x00270008</c> but never as <c>0</c>, so zero would read as a success and turn
    /// a silently skipped item into a reported recycle.
    /// </remarks>
    internal const int Unreported = int.MinValue;

    private readonly string[] _pathsInQueueOrder;
    private readonly int _queuedCount;
    private readonly Dictionary<string, (int HResult, string? RecycleBinPath)> _outcomes;

    /// <summary>
    /// Index of the first path in <see cref="_pathsInQueueOrder"/> the Shell has not reported on yet.
    /// </summary>
    /// <remarks>
    /// Only ever moves forward, which is what makes the fallback cheap. A verdict is written once per
    /// path and never cleared, so an index skipped as reported can never become unreported again.
    /// </remarks>
    private int _firstUnreported;

    /// <summary>
    /// Creates a sink for one round of deletes.
    /// </summary>
    /// <param name="pathsInQueueOrder">
    /// Full paths queued this round, in the order they were queued. Retained rather than copied, so the
    /// caller must not modify it for as long as the sink is in use.
    /// </param>
    internal RecycleProgressSink(IReadOnlyList<string> pathsInQueueOrder)
        : this(ToArray(pathsInQueueOrder), pathsInQueueOrder?.Count ?? 0)
    {
    }

    /// <summary>
    /// Creates a sink over the first <paramref name="queuedCount"/> entries of a buffer the caller
    /// intends to refill for the next round.
    /// </summary>
    /// <param name="pathsInQueueOrder">
    /// Buffer holding at least <paramref name="queuedCount"/> queued paths. Only the first
    /// <paramref name="queuedCount"/> entries are read; the rest may hold anything.
    /// </param>
    /// <param name="queuedCount">How many leading entries of the buffer were queued this round.</param>
    internal RecycleProgressSink(string[] pathsInQueueOrder, int queuedCount)
    {
        ArgumentNullException.ThrowIfNull(pathsInQueueOrder);
        ArgumentOutOfRangeException.ThrowIfNegative(queuedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(queuedCount, pathsInQueueOrder.Length);

        _pathsInQueueOrder = pathsInQueueOrder;
        _queuedCount = queuedCount;
        _outcomes = new Dictionary<string, (int, string?)>(queuedCount, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < queuedCount; i++)
        {
            _outcomes[pathsInQueueOrder[i]] = (Unreported, null);
        }
    }

    private static string[] ToArray(IReadOnlyList<string> pathsInQueueOrder)
    {
        ArgumentNullException.ThrowIfNull(pathsInQueueOrder);
        return pathsInQueueOrder as string[] ?? [.. pathsInQueueOrder];
    }

    /// <summary>
    /// Reads the Shell result for one queued path.
    /// </summary>
    /// <param name="path">Full path as it was queued.</param>
    /// <param name="hResult">
    /// The Shell's <c>hrDelete</c>. Success means the severity bit is clear; the Shell reports a
    /// successful delete as <c>0x00270008</c>, not as <c>S_OK</c>, so comparing against zero is
    /// wrong.
    /// </param>
    /// <param name="recycleBinPath">
    /// Path the Shell created inside the Recycle Bin, or <see langword="null"/> when the Shell did
    /// not report one. A <see langword="null"/> value is not by itself a failure: the Shell omits it
    /// for some successful deletes, for example on very long paths.
    /// </param>
    /// <returns><see langword="true"/> when the Shell reported an outcome for this path.</returns>
    internal bool TryGetOutcome(string path, out int hResult, out string? recycleBinPath)
    {
        if (_outcomes.TryGetValue(path, out var outcome) && outcome.HResult != Unreported)
        {
            hResult = outcome.HResult;
            recycleBinPath = outcome.RecycleBinPath;
            return true;
        }

        hResult = Unreported;
        recycleBinPath = null;
        return false;
    }

    public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated)
    {
        try
        {
            var path = ResolvePath(psiItem);
            if (path is null || !_outcomes.ContainsKey(path))
            {
                path = NextUnrecordedPath();
            }

            if (path is null)
            {
                return;
            }

            _outcomes[path] = (hrDelete.Value, ResolvePath(psiNewlyCreated));
        }
        catch (Exception)
        {
            // This runs as a COM callback. Letting anything escape crosses the interop boundary,
            // where the runtime's handling is not ours to choose, so the item is simply left
            // unreported: the caller sees no outcome, retries it, and fails it with a real code.
        }
    }

    public void StartOperations()
    {
    }

    public void FinishOperations(HRESULT hrResult)
    {
    }

    public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
    {
    }

    public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated)
    {
    }

    public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
    {
    }

    public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
    {
    }

    public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
    {
    }

    public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated)
    {
    }

    public void PreDeleteItem(uint dwFlags, IShellItem psiItem)
    {
    }

    public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName)
    {
    }

    public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName, PCWSTR pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem)
    {
    }

    public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
    {
    }

    public void ResetTimer()
    {
    }

    public void PauseTimer()
    {
    }

    public void ResumeTimer()
    {
    }

    /// <summary>
    /// Hands out the earliest queued path the Shell has not reported on, which is the best guess at
    /// which item the callback is about.
    /// </summary>
    /// <remarks>
    /// The caller writes the outcome it just received onto the returned path, which is what lets the
    /// scan resume from where it stopped instead of re-walking the batch for every unrecognised
    /// callback.
    /// </remarks>
    private string? NextUnrecordedPath()
    {
        while (_firstUnreported < _queuedCount)
        {
            var path = _pathsInQueueOrder[_firstUnreported];
            if (_outcomes[path].HResult == Unreported)
            {
                return path;
            }

            _firstUnreported++;
        }

        return null;
    }

    private static string? ResolvePath(IShellItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var name = default(PWSTR);
        try
        {
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out name);
            return name.ToString();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            // GetDisplayName allocates with CoTaskMem; PWSTR itself does not release it.
            if (name != default)
            {
                PInvoke.CoTaskMemFree((void*)name);
            }
        }
    }
}
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Utils;

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
    private readonly Dictionary<string, (int HResult, string? RecycleBinPath)> _outcomes;

    /// <summary>
    /// Creates a sink for one round of deletes.
    /// </summary>
    /// <param name="pathsInQueueOrder">Full paths queued this round, in the order they were queued.</param>
    internal RecycleProgressSink(IReadOnlyList<string> pathsInQueueOrder)
    {
        ArgumentNullException.ThrowIfNull(pathsInQueueOrder);

        _pathsInQueueOrder = new string[pathsInQueueOrder.Count];
        _outcomes = new Dictionary<string, (int, string?)>(pathsInQueueOrder.Count, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < pathsInQueueOrder.Count; i++)
        {
            _pathsInQueueOrder[i] = pathsInQueueOrder[i];
            _outcomes[pathsInQueueOrder[i]] = (Unreported, null);
        }
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

    private string? NextUnrecordedPath()
    {
        foreach (var path in _pathsInQueueOrder)
        {
            if (_outcomes[path].HResult == Unreported)
            {
                return path;
            }
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
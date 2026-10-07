using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace RecycleBinToolkit;

/// <summary>
/// Owns COM initialisation and a single Shell operation object for one STA thread.
/// </summary>
/// <remarks>
/// The operation object is created once and reused for every round, which is what makes batched
/// deletion cheap: creating it costs far more than performing on it. Every method must be called
/// on the thread that created the instance, because <c>CoUninitialize</c> in <see cref="Dispose"/>
/// has to pair with the matching <c>CoInitializeEx</c>.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed unsafe class ShellOperationContext : IDisposable
{
    private const int Success = 0;

    private const int ChangedModeHResult = unchecked((int)0x80010106);

    private const FILEOPERATION_FLAGS RecycleFlags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO |
        FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE |
        FILEOPERATION_FLAGS.FOF_NOCONFIRMATION |
        FILEOPERATION_FLAGS.FOF_NOERRORUI |
        FILEOPERATION_FLAGS.FOF_SILENT |
        FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR;


    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    private IFileOperation? _fileOperation;
    private bool _uninitializeRequired;
    private bool _disposed;

    private ShellOperationContext()
    {
    }

    /// <summary>
    /// Initialises COM for the current thread as single-threaded apartment and creates the Shell
    /// operation object.
    /// </summary>
    /// <returns>A context that must be disposed on the same thread.</returns>
    internal static ShellOperationContext Create()
    {
        var context = new ShellOperationContext();

        try
        {
            var coInit = PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED);
            if (coInit.Failed)
            {
                // RPC_E_CHANGED_MODE means COM is already up in another mode on this thread, which
                // is still usable; anything else is fatal.
                if (coInit.Value != ChangedModeHResult)
                {
                    RecycleBinThrowHelper.ThrowWorkerUnavailable(
                        new COMException($"CoInitializeEx failed with 0x{coInit.Value:X8}.", coInit.Value));
                }
            }
            else
            {
                // S_OK and S_FALSE both increment the apartment count, and both need a matching
                // CoUninitialize. Only a changed mode or a hard failure leaves nothing to undo.
                context._uninitializeRequired = true;
            }

            var hrCreate = PInvoke.CoCreateInstance(ClsidFileOperation, null, CLSCTX.CLSCTX_INPROC_SERVER, out IFileOperation fileOperation);
            if (hrCreate.Failed)
            {
                RecycleBinThrowHelper.ThrowWorkerUnavailable(
                    new COMException($"Could not create the Shell file operation object (0x{hrCreate.Value:X8}).", hrCreate.Value));
            }

            context._fileOperation = fileOperation;
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies the recycle flags. Must be called before every queueing round.
    /// </summary>
    internal void SetFlags() => Operation.SetOperationFlags(RecycleFlags);

    /// <summary>
    /// The operation object, or a fault if the context has already been disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="Dispose"/> nulls the field once the object is released, so the members go through this
    /// rather than touching it directly. Letting a disposed context keep handing out the object it just
    /// released would fault deeper down with a message about a COM pointer, which says nothing about
    /// what the caller actually did.
    /// </remarks>
    private IFileOperation Operation
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _fileOperation!;
        }
    }

    /// <summary>
    /// Creates a Shell item for a path, falling back to its 8.3 short name when the Shell refuses
    /// the full path.
    /// </summary>
    /// <param name="fullPath">Normalised absolute path.</param>
    /// <param name="item">The created item, or <see langword="null"/> when creation failed.</param>
    /// <param name="hResult">
    /// The Shell's error code, or 0 on success. On failure this is the HRESULT from the full-path
    /// attempt, so a caller can distinguish "no such file" from a Shell refusal.
    /// </param>
    /// <returns><see langword="true"/> when an item was created.</returns>
    internal bool TryCreateShellItem(string fullPath, out IShellItem? item, out int hResult)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hrItem = PInvoke.SHCreateItemFromParsingName<IShellItem>(fullPath, null, out item);
        if (hrItem.Succeeded)
        {
            hResult = Success;
            return true;
        }

        hResult = TryShortPathFallback(fullPath, out item) ? Success : hrItem.Value;
        return hResult == Success;
    }

    /// <summary>
    /// Queues an item for deletion and routes its callbacks to <paramref name="sink"/>, which is how
    /// the per-item <c>hrDelete</c> becomes observable.
    /// </summary>
    /// <param name="item">Item to delete.</param>
    /// <param name="sink">Sink receiving this item's callbacks.</param>
    /// <param name="hResult">0 on success, otherwise the Shell's error code.</param>
    /// <returns><see langword="true"/> when the item was queued.</returns>
    internal bool TryQueue(IShellItem item, IFileOperationProgressSink sink, out int hResult)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sink);

        try
        {
            Operation.DeleteItem(item, sink);
            hResult = Success;
            return true;
        }
        catch (COMException ex)
        {
            hResult = ex.HResult;
            return false;
        }
    }

    /// <summary>
    /// Runs every queued item.
    /// </summary>
    /// <remarks>
    /// The generated signature returns <see langword="void"/>: the Shell's overall HRESULT is not
    /// surfaced to the caller, so per-item outcomes must come from a progress sink.
    /// </remarks>
    internal void Perform() => Operation.PerformOperations();

    /// <summary>
    /// Releases the operation object and then the thread's COM apartment. Must run on the creating thread.
    /// </summary>
    /// <remarks>
    /// The order is not incidental. An apartment torn down while a wrapper it created is still alive
    /// leaves the release to happen at an arbitrary later point, on whatever thread the finalizer runs,
    /// which for an object that must be released on one thread is a leak rather than a cleanup. So the
    /// object goes first, deterministically, and only then is the apartment released.
    /// <para>
    /// In the library this runs only from <see cref="Create"/>'s own failure path. The worker holds its
    /// apartment for the lifetime of the process and has no shutdown path, so a healthy context is never
    /// disposed — see the note at the worker's loop. The ordering still has to be right here rather than
    /// being left to chance, because the one path that does run it must not be the one that leaks.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_fileOperation is not null)
        {
            Marshal.ReleaseComObject(_fileOperation);
            _fileOperation = null;
        }

        if (_uninitializeRequired)
        {
            PInvoke.CoUninitialize();
            _uninitializeRequired = false;
        }
    }

    private static bool TryShortPathFallback(string fullPath, out IShellItem? item)
    {
        item = null;

        var shortPath = ShortPathResolver.TryResolveShort(fullPath);
        if (shortPath is null)
        {
            return false;
        }

        var hrItem = PInvoke.SHCreateItemFromParsingName<IShellItem>(shortPath, null, out item);
        return hrItem.Succeeded;
    }
}
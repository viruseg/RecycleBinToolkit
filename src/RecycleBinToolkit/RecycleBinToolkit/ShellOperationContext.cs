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
                        // Carried as an inner exception because it is the only type that takes an HRESULT
                        // verbatim; it never escapes, since ThrowWorkerUnavailable wraps it.
#pragma warning disable CA2201 // Reserved exception type, used only as an HRESULT carrier.
                        new COMException($"CoInitializeEx failed with 0x{coInit.Value:X8}.", coInit.Value));
#pragma warning restore CA2201
                }
            }
            else
            {
                // S_OK and S_FALSE both increment the apartment count, and both need a matching
                // CoUninitialize. Only a changed mode or a hard failure leaves nothing to undo.
                context._uninitializeRequired = true;
            }

            context.CreateOperationObject();
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates the Shell operation object and takes ownership of it.
    /// </summary>
    private void CreateOperationObject()
    {
        var hrCreate = PInvoke.CoCreateInstance(ClsidFileOperation, null, CLSCTX.CLSCTX_INPROC_SERVER, out IFileOperation fileOperation);
        if (hrCreate.Failed)
        {
            RecycleBinThrowHelper.ThrowWorkerUnavailable(
                // Carried as an inner exception because it is the only type that takes an HRESULT
                // verbatim; it never escapes, since ThrowWorkerUnavailable wraps it.
#pragma warning disable CA2201 // Reserved exception type, used only as an HRESULT carrier.
                new COMException($"Could not create the Shell file operation object (0x{hrCreate.Value:X8}).", hrCreate.Value));
#pragma warning restore CA2201
        }

        _fileOperation = fileOperation;
    }

    /// <summary>
    /// Throws away the operation object and builds a fresh one.
    /// </summary>
    /// <remarks>
    /// The object queues what it is given and only clears its queue by performing it. So a perform that
    /// fails leaves whatever was queued still queued, and because the context is reused for every
    /// operation in the process, the next caller's <c>PerformOperations</c> would carry out the previous
    /// caller's deletes and report the results against its own batch. Recreating the object is the only
    /// way to drop a queue the Shell would not clear.
    /// <para>
    /// Only the operation object is replaced. The apartment stays as it is: reinitialising it here would
    /// unbalance the <c>CoInitializeEx</c> count this context is holding, and the apartment is not what
    /// went wrong.
    /// </para>
    /// </remarks>
    internal void RecreateOperationObject()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ReleaseOperationObject();
        CreateOperationObject();
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
        catch (Exception ex) when (IsShellRefusal(ex))
        {
            hResult = ex.HResult;
            return false;
        }
    }

    /// <summary>
    /// Runs every queued item.
    /// </summary>
    /// <remarks>
    /// The generated signature returns <see langword="void"/>, so the Shell's overall HRESULT is not
    /// surfaced to the caller and per-item outcomes must come from a progress sink — but a failing
    /// HRESULT is still raised, as an exception whose type depends on the code.
    /// </remarks>
    internal void Perform() => Operation.PerformOperations();

    /// <summary>
    /// Whether an exception is the Shell declining a call rather than the code being wrong.
    /// </summary>
    /// <remarks>
    /// The generated interop raises a failing HRESULT through <c>ThrowExceptionForHR</c>, which does not
    /// always produce a <see cref="COMException"/>. Measured on this build:
    /// <c>0x8000FFFF</c> and <c>0x80004005</c> give <see cref="COMException"/>, <c>0x80070057</c> gives
    /// <see cref="ArgumentException"/>, <c>0x80070005</c> gives <see cref="UnauthorizedAccessException"/>,
    /// <c>0x8007000E</c> gives <see cref="OutOfMemoryException"/> and <c>0x80004001</c> gives
    /// <see cref="NotImplementedException"/>.
    /// <para>
    /// A catch clause naming only <see cref="COMException"/> therefore catches the minority of refusals:
    /// one that raises <see cref="ArgumentException"/> escapes a method documented never to throw for a
    /// per-item failure and takes the whole batch with it. Every one of those types carries the Shell's
    /// own <see cref="Exception.HResult"/>, which is the code a caller is told about, so they all belong
    /// here rather than being mistaken for a defect in this library.
    /// </para>
    /// <para>
    /// <see cref="ObjectDisposedException"/> is deliberately absent: a context that has been disposed is
    /// a defect in how it is used, not the Shell declining, and should not be recorded as an item failure.
    /// </para>
    /// </remarks>
    internal static bool IsShellRefusal(Exception exception) => exception is
        COMException
        or ArgumentException
        or UnauthorizedAccessException
        or OutOfMemoryException
        or NotImplementedException;

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

        ReleaseOperationObject();

        if (_uninitializeRequired)
        {
            PInvoke.CoUninitialize();
            _uninitializeRequired = false;
        }
    }

    /// <summary>
    /// Releases the operation object without touching the apartment, leaving the context able to build
    /// another one.
    /// </summary>
    private void ReleaseOperationObject()
    {
        if (_fileOperation is null)
        {
            return;
        }

        Marshal.ReleaseComObject(_fileOperation);
        _fileOperation = null;
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
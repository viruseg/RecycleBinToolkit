using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Utils;

/// <summary>
/// Process-wide worker that owns the Shell COM apartment.
/// </summary>
/// <remarks>
/// Shell file operations require a single-threaded apartment, and every COM object involved must
/// be created and used on one thread. Creating a thread per call — as a naïve implementation does
/// — makes thread startup and COM initialisation the dominant cost when files are deleted in a
/// loop, so this worker is created once, warmed up before the first request, and reused.
/// <para>
/// Every call is dispatched here even when the caller is already on an STA thread. That keeps
/// <c>CoInitializeEx</c> from running in an incompatible apartment mode and keeps the caller free
/// of Shell re-entrancy, at the cost of one thread hop that the Shell's own latency dominates.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class RecycleBinWorker
{
    private static readonly object Gate = new();
    private static Worker? _worker;

    /// <summary>
    /// Managed id of the worker thread, or 0 before the first submit.
    /// </summary>
    internal static int ThreadId => Volatile.Read(ref _worker)?.ThreadId ?? 0;

    /// <summary>
    /// Hands a batch to the worker and completes when the Shell has finished with it.
    /// </summary>
    /// <param name="targets">Paths to move to the Recycle Bin.</param>
    /// <param name="options">Retry and verification settings.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait. Cancellation never tears the worker down: a request already handed to the
    /// Shell runs to completion, so a cancelled caller cannot leave a half-performed operation.
    /// </param>
    /// <returns>The outcome of the batch.</returns>
    internal static Task<RecycleBinResult> SubmitAsync(
        IReadOnlyList<RecycleTarget> targets,
        RecycleBinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RecycleBinResult>(cancellationToken);
        }

        var request = new Request(targets, options.Normalize());
        EnsureWorker().Enqueue(request);

        return cancellationToken.CanBeCanceled
            ? AwaitWithCancellation(request, cancellationToken)
            : request.Completion.Task;
    }

    private static Worker EnsureWorker()
    {
        var existing = Volatile.Read(ref _worker);
        if (existing is not null)
        {
            return existing;
        }

        lock (Gate)
        {
            return _worker ??= new Worker();
        }
    }

    private static async Task<RecycleBinResult> AwaitWithCancellation(Request request, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancelled);

        if (await Task.WhenAny(request.Completion.Task, cancelled.Task).ConfigureAwait(false) == cancelled.Task)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return await request.Completion.Task.ConfigureAwait(false);
    }

    private sealed class Request
    {
        internal Request(IReadOnlyList<RecycleTarget> targets, RecycleBinOptions options)
        {
            Targets = targets;
            Options = options;
        }

        internal IReadOnlyList<RecycleTarget> Targets { get; }

        internal RecycleBinOptions Options { get; }

        internal TaskCompletionSource<RecycleBinResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed unsafe class Worker
    {
        private readonly Queue<Request> _queue = [];
        private readonly object _queueGate = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly ManualResetEventSlim _warm = new(false);

        internal Worker()
        {
            var thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RecycleBinWorker"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            ThreadId = thread.ManagedThreadId;

            // Block until the apartment is warm, so the first caller never races Shell
            // initialisation and thread startup is paid exactly once.
            _warm.Wait();

            if (StartupFailure is not null)
            {
                // Leaving a dead worker in the cache would make every later call in the process fail
                // the same way forever, even ones the Shell would now accept. Not caching it means
                // the next submit builds a fresh worker and can succeed.
                RecycleBinThrowHelper.ThrowWorkerUnavailable(StartupFailure);
            }
        }

        internal int ThreadId { get; }

        private Exception? StartupFailure { get; set; }

        internal void Enqueue(Request request)
        {
            if (StartupFailure is not null)
            {
                RecycleBinThrowHelper.ThrowWorkerUnavailable(StartupFailure);
            }

            lock (_queueGate)
            {
                _queue.Enqueue(request);
            }

            _signal.Release();
        }

        private void Run()
        {
            ShellOperationContext? context = null;

            try
            {
                context = ShellOperationContext.Create();
                CreateMessageQueue();
                _runner = new RecycleOperationRunner(context);
            }
            catch (Exception ex)
            {
                StartupFailure = ex;
                _warm.Set();
                FailPending(ex);
                return;
            }

            _warm.Set();

            try
            {
                while (true)
                {
                    _signal.Wait();

                    while (TryDequeue(out var request))
                    {
                        try
                        {
                            request!.Completion.TrySetResult(_runner.Run(request.Targets, request.Options, CancellationToken.None));
                        }
                        catch (Exception ex)
                        {
                            request!.Completion.TrySetException(ex);
                        }
                    }
                }
            }
            finally
            {
                context.Dispose();
                FailPending(new ObjectDisposedException(nameof(RecycleBinWorker)));
            }
        }

        private RecycleOperationRunner? _runner;

        private bool TryDequeue(out Request? request)
        {
            lock (_queueGate)
            {
                if (_queue.Count == 0)
                {
                    request = null;
                    return false;
                }

                request = _queue.Dequeue();
                return true;
            }
        }

        private void FailPending(Exception exception)
        {
            while (TryDequeue(out var request))
            {
                request!.Completion.TrySetException(exception);
            }
        }

        /// <summary>
        /// Creates the thread's message queue. The Shell posts work to this thread while performing,
        /// so a queueless thread would behave unpredictably even though the operations themselves
        /// pump internally.
        /// </summary>
        private static void CreateMessageQueue()
        {
            var message = default(MSG);
            PInvoke.PeekMessage(&message, default, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        }
    }

}

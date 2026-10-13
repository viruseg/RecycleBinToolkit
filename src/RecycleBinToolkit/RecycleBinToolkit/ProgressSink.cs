using System.Diagnostics;
using System.Threading.Channels;

namespace RecycleBinToolkit;

/// <summary>
/// The queue behind a caller's progress handler: the reports an operation makes go in one end, and a
/// thread of its own takes them out and hands them over one at a time.
/// </summary>
/// <remarks>
/// Unbounded, so no producer ever waits for the handler — that is the whole reason the operation's own
/// throughput is not the caller's to spend. Read by one thread and written by any number, because a
/// restore runs its volumes in parallel and an empty runs its disks in parallel.
/// <para>
/// One queue per call. A settings object a caller keeps and hands to a second call is handed to two calls
/// that may overlap, and one queue between them would have the first call's completion close the second's
/// and silently drop every report the second made after it.
/// </para>
/// <para>
/// The channel carries <see cref="object"/> and the subclass supplies the typed cast, rather than three
/// near-identical readers. A handler that throws ends delivery rather than the operation: the reports are
/// the library's answers and they are already decided by the time they get here.
/// </para>
/// </remarks>
internal abstract class ProgressSink
{
    private readonly Action<object> _deliver;
    private readonly Stopwatch _since;
    private readonly Channel<object> _channel;
    private readonly Thread _pump;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _next;

    /// <summary>
    /// Creates a sink that hands each report to <paramref name="deliver"/>, and starts the thread that
    /// will do it.
    /// </summary>
    /// <param name="deliver">Casts the report to its own type and calls the caller's handler.</param>
    protected ProgressSink(Action<object> deliver)
    {
        _deliver = deliver;
        _since = Stopwatch.StartNew();
        _channel = Channel.CreateUnbounded<object>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        // A thread of its own, started here rather than on the first report because the first report can
        // come from several engine threads at once and a check-then-start between them starts two pumps on
        // one channel — the one thing a channel created for a single reader must not be handed.
        //
        // Started as a thread rather than as a long-running task because a queued task may be run inline by
        // whichever thread queues it, and that would put the caller's own handler on the caller's own
        // thread, which is the one thing this class exists to prevent. A background thread costs nothing
        // when the process exits, and cannot be inlined.
        _pump = new Thread(PumpAsync)
        {
            IsBackground = true,
            Name = "RecycleBinToolkit progress"
        };

        _pump.Start();
    }

    /// <summary>Whether this queue is still taking reports.</summary>
    protected internal bool Running { get; private set; } = true;

    /// <summary>Total the operation is reporting against, set before its first report.</summary>
    internal int Count { get; private set; }

    /// <summary>What to do about a handler that threw.</summary>
    internal Action<Exception>? Fault { get; set; }

    /// <summary>Time since this call armed the queue, which is not the time the caller spent waiting.</summary>
    internal TimeSpan Elapsed => _since.Elapsed;

    /// <summary>
    /// Hands out the next number, from one counter every thread of the operation shares.
    /// </summary>
    internal int NextIndex() => Interlocked.Increment(ref _next);

    /// <summary>
    /// Fixes the total the reports carry, before the first of them is made.
    /// </summary>
    internal void SetCount(int count) => Count = count;

    /// <summary>
    /// Queues one report.
    /// </summary>
    /// <remarks>
    /// A refused write is not reported. It cannot happen while the operation runs, and after it — or after a
    /// handler threw — there is nothing to report it to, because the only channel it could travel on is the
    /// one that refused it.
    /// </remarks>
    internal void Report(object item) => _channel.Writer.TryWrite(item);

    /// <summary>
    /// Completes the queue and, when the caller asked to wait for it, returns once it has been drained.
    /// </summary>
    /// <param name="wait">Whether to wait for the handler to have been called for every report.</param>
    internal Task CompleteAsync(bool wait)
    {
        Running = false;
        _channel.Writer.TryComplete();

        return wait ? _drained.Task : Task.CompletedTask;
    }

    /// <summary>
    /// Delivers every report, in the order they arrived, until the queue is completed and drained.
    /// </summary>
    /// <remarks>
    /// Runs to completion even when the operation was cancelled or raised, because a report the caller was
    /// already told about is still a report.
    /// </remarks>
    private void PumpAsync()
    {
        try
        {
            var reader = _channel.Reader;

            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        _deliver(item);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Fault?.Invoke(ex);
                        }
                        catch (Exception)
                        {
                            // A failure handler that throws has nowhere to report to either: the operation has
                            // already returned, and the caller's handler is the thing that is broken.
                        }

                        return;
                    }
                }
            }
        }
        finally
        {
            _drained.TrySetResult();
        }
    }

    /// <summary>
    /// A new queue over the same handler, which is what a second call through one settings object gets.
    /// </summary>
    internal abstract ProgressSink Restart();
}

/// <summary>The sink behind a caller's recycle progress handler.</summary>
internal sealed class RecycleProgressSink : ProgressSink
{
    internal RecycleProgressSink(Action<RecycleProgress>? handler)
        : base(item => handler?.Invoke((RecycleProgress)item))
    {
        Handler = handler;
    }

    internal Action<RecycleProgress>? Handler { get; }

    internal override ProgressSink Restart() => new RecycleProgressSink(Handler) { Fault = Fault };
}

/// <summary>The sink behind a caller's restore progress handler.</summary>
internal sealed class RestoreProgressSink : ProgressSink
{
    internal RestoreProgressSink(Action<RestoreProgress>? handler)
        : base(item => handler?.Invoke((RestoreProgress)item))
    {
        Handler = handler;
    }

    internal Action<RestoreProgress>? Handler { get; }

    internal override ProgressSink Restart() => new RestoreProgressSink(Handler) { Fault = Fault };
}

/// <summary>The sink behind a caller's empty progress handler.</summary>
internal sealed class EmptyProgressSink : ProgressSink
{
    internal EmptyProgressSink(Action<EmptyProgress>? handler)
        : base(item => handler?.Invoke((EmptyProgress)item))
    {
        Handler = handler;
    }

    internal Action<EmptyProgress>? Handler { get; }

    internal override ProgressSink Restart() => new EmptyProgressSink(Handler) { Fault = Fault };
}
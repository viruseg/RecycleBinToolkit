using System.Diagnostics;
using System.Threading.Channels;

namespace RecycleBinToolkit;

/// <summary>
/// The queue behind a caller's progress handler: the events an operation creates go in one end, and a
/// thread of its own takes them out and hands them over one at a time.
/// </summary>
/// <remarks>
/// Unbounded, so no producer ever waits for the handler — that is the whole reason the operation's own
/// throughput is not the caller's to spend. Read by one thread and written by any number, because a
/// restore runs its volumes in parallel and an empty runs its disks in parallel.
/// <para>
/// The channel carries <see cref="object"/> and the subclass supplies the typed cast, rather than three
/// near-identical readers. A handler that throws ends delivery rather than the operation: the events are
/// the library's answers and they are already decided by the time they get here.
/// </para>
/// </remarks>
internal abstract class ProgressSink
{
    private readonly Action<object> _deliver;
    private readonly Stopwatch _since;
    private readonly Channel<object> _channel;
    private int _next;

    private Task? _reader;

    /// <summary>
    /// Creates a sink that hands each event to <paramref name="deliver"/>.
    /// </summary>
    /// <param name="deliver">Casts the event to its own type and calls the caller's handler.</param>
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
    }

    /// <summary>Whether this sink is still taking events.</summary>
    protected bool Running { get; private set; } = true;

    /// <summary>Total the operation is reporting against, set before its first event.</summary>
    internal int Count { get; private set; }

    /// <summary>What to do about a handler that threw.</summary>
    internal Action<Exception>? Fault { get; set; }

    /// <summary>Time since this sink was built, which is not the time the caller spent waiting.</summary>
    internal TimeSpan Elapsed => _since.Elapsed;

    /// <summary>
    /// Hands out the next number, from one counter every thread of the operation shares.
    /// </summary>
    internal int NextIndex() => Interlocked.Increment(ref _next);

    /// <summary>
    /// Fixes the total the events report against, before the first of them is created.
    /// </summary>
    internal void SetCount(int count) => Count = count;

    /// <summary>
    /// Queues one event. The result is dropped: an unbounded channel refuses a write only once its writer
    /// is completed, and that happens after the operation's last event rather than during it.
    /// </summary>
    internal void Report(object item)
    {
        _channel.Writer.TryWrite(item);
        _reader ??= Task.Factory.StartNew(
            PumpAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Completes the queue and, when the caller asked to wait for it, returns once it has been drained.
    /// </summary>
    /// <param name="wait">Whether to wait for the handler to have been called for every event.</param>
    internal Task CompleteAsync(bool wait)
    {
        Running = false;
        _channel.Writer.TryComplete();

        return wait ? _reader ?? Task.CompletedTask : Task.CompletedTask;
    }

    /// <summary>
    /// Delivers every event, in the order they arrived, until the queue is completed and drained.
    /// </summary>
    /// <remarks>
    /// Runs to completion even when the operation was cancelled or raised, because an event the caller
    /// was already told about is still an event.
    /// </remarks>
    private async Task PumpAsync()
    {
        var reader = _channel.Reader;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
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

    /// <summary>
    /// Returns this sink while it is still taking events, and a new one carrying the same handler once it
    /// is not — which is what makes one options instance usable for a second call.
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

    internal override ProgressSink Restart() =>
        Running ? this : new RecycleProgressSink(Handler) { Fault = Fault };
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

    internal override ProgressSink Restart() =>
        Running ? this : new RestoreProgressSink(Handler) { Fault = Fault };
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

    internal override ProgressSink Restart() =>
        Running ? this : new EmptyProgressSink(Handler) { Fault = Fault };
}
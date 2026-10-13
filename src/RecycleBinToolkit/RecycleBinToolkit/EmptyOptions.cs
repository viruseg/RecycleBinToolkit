namespace RecycleBinToolkit;

/// <summary>Settings for <see cref="RecycleBin.EmptyAsync(string?, CancellationToken)"/>.</summary>
/// <remarks>
/// <see langword="null"/> means <see cref="Default"/>.
/// </remarks>
public sealed class EmptyOptions : RecycleBinOptions
{
    /// <summary>Shared instance carrying the documented defaults.</summary>
    public static EmptyOptions Default { get; } = new();

    /// <summary>
    /// Called once per entry the empty went after, on a thread of its own, so the empty does not wait for
    /// it. Defaults to <see langword="null"/>, which reports nothing and costs nothing.
    /// </summary>
    /// <remarks>
    /// Setting this makes the empty read every volume's bin one extra time, to know how many entries to
    /// report against. Reports may still arrive after the call returns unless
    /// <see cref="WaitForProgressCallbacks"/> is set.
    /// </remarks>
    public new Action<EmptyProgress>? OnItemProcessed
    {
        get => (base.OnItemProcessed as EmptyProgressSink)?.Handler;
        init => base.OnItemProcessed = value is null ? null : new EmptyProgressSink(value);
    }

    /// <summary>
    /// Called once with whatever <see cref="OnItemProcessed"/> threw, after which that handler is not
    /// called again. Defaults to <see langword="null"/>, which drops it.
    /// </summary>
    public new Action<Exception>? OnProgressFailed
    {
        get => base.OnProgressFailed;
        init => base.OnProgressFailed = value;
    }

    /// <summary>
    /// Whether the call returns only once every report has been delivered. Defaults to
    /// <see langword="false"/>, so the empty is never held up by the handler.
    /// </summary>
    public new bool WaitForProgressCallbacks
    {
        get => base.WaitForProgressCallbacks;
        init => base.WaitForProgressCallbacks = value;
    }
}
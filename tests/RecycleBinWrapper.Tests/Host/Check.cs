namespace RecycleBinWrapper.Tests.Host;

/// <summary>
/// Assertion helpers. Every failure carries the reason so a red run is readable without
/// attaching a debugger.
/// </summary>
internal static class Check
{
    internal static void True(bool condition, string because)
    {
        if (!condition)
        {
            throw new CheckFailedException($"expected true: {because}");
        }
    }

    internal static void False(bool condition, string because)
    {
        if (condition)
        {
            throw new CheckFailedException($"expected false: {because}");
        }
    }

    internal static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailedException($"expected {Format(expected)} but got {Format(actual)}: {because}");
        }
    }

    internal static void NotNull(object? value, string because)
    {
        if (value is null)
        {
            throw new CheckFailedException($"expected a value but got null: {because}");
        }
    }

    /// <summary>
    /// Asserts that <paramref name="action"/> throws <typeparamref name="TException"/>.
    /// </summary>
    /// <returns>The thrown exception, so callers can assert on it further.</returns>
    internal static TException Throws<TException>(Action action, string because)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception unexpected)
        {
            throw new CheckFailedException(
                $"expected {typeof(TException).Name} but got {unexpected.GetType().Name} ({unexpected.Message}): {because}");
        }

        throw new CheckFailedException($"expected {typeof(TException).Name} but nothing was thrown: {because}");
    }

    private static string Format<T>(T value) => value switch
    {
        null => "<null>",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? "<null>"
    };
}

/// <summary>
/// Raised when an assertion does not hold.
/// </summary>
internal sealed class CheckFailedException : Exception
{
    internal CheckFailedException(string message) : base(message)
    {
    }
}
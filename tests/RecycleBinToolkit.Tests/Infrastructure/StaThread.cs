using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Runs a synchronous body on a dedicated single-threaded apartment thread and returns its result.
/// </summary>
/// <remarks>
/// Shell operations belong to one STA thread, so a test that drives the Shell directly must not run
/// on the test runner's own MTA thread.
/// </remarks>
[SupportedOSPlatform("windows6.0.6000")]
internal static class StaThread
{
    internal static T Run<T>(Func<T> body, TimeSpan? timeout = null)
    {
        var result = default(T)!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "StaThread.Run"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(timeout ?? TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException($"The STA test thread did not finish within the timeout; body: {body.Method.Name}.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }
}
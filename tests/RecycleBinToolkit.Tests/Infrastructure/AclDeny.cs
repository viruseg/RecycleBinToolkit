using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace RecycleBinToolkit.Tests.Infrastructure;

/// <summary>
/// Applies and removes a deny entry in a path's access control list, for the cases that need a removal the
/// account really may not perform.
/// </summary>
internal static class AclDeny
{
    private static readonly string Identity = WindowsIdentity.GetCurrent().Name;

    internal static bool TryDeny(string path, string rights, out string error) =>
        TryRun([path, "/deny", $"{Identity}:({rights})"], out error);

    internal static void Remove(string path) =>
        TryRun([path, "/remove:d", Identity], out _);

    private static bool TryRun(string[] arguments, out string error)
    {
        try
        {
            var startInfo = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                error = "icacls did not start";
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            var errorOutput = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var message = $"{output}{errorOutput}".Trim();
            error = process.ExitCode == 0
                ? string.Empty
                : message.Length > 300 ? message[..300] : message;

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }
}

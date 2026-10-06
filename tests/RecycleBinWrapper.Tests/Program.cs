using RecycleBinWrapper.Tests.Host;
using RecycleBinWrapper.Tests.Suites;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Recycle Bin tests are Windows-only.");
    return 2;
}

return await TestRunner.RunAsync(args, WorkspaceSuite.Cases.Concat(VolumeSuite.Cases).ToArray());
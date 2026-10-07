using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Suites;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Recycle Bin tests are Windows-only.");
    return 2;
}

return await TestRunner.RunAsync(args,
    WorkspaceSuite.Cases
        .Concat(VolumeSuite.Cases)
        .Concat(ContractSuite.Cases)
        .Concat(ShellSuite.Cases)
        .Concat(WorkerSuite.Cases)
        .Concat(RunnerSuite.Cases)
        .Concat(FacadeSuite.Cases)
        .Concat(WorkflowSuite.Cases)
        .Concat(StressSuite.Cases)
        .Concat(LongPathSuite.Cases)
        .ToArray());
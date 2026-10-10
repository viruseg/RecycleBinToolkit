using RecycleBinToolkit.Tests.Host;
using RecycleBinToolkit.Tests.Suites;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Recycle Bin tests are Windows-only.");
    return 2;
}

return await TestRunner.RunAsync(args,
    ScratchSuite.Cases
        .Concat(WorkspaceSuite.Cases)
        .Concat(VolumeSuite.Cases)
        .Concat(ContractSuite.Cases)
        .Concat(FacadeSuite.Cases)
        .Concat(WorkflowSuite.Cases)
        .Concat(StressSuite.Cases)
        .Concat(NativeBinSuite.Cases)
        .Concat(ListerSuite.Cases)
        .Concat(EmptySuite.Cases)
        .Concat(StatusSuite.Cases)
        .Concat(RegistrySuite.Cases)
        .ToArray());
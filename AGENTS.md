# Working in this repository

Windows-only, .NET 10, no external NuGet packages beyond `Microsoft.Windows.CsWin32` (a build-time
source generator). The test project is a console application with a hand-rolled harness, not a test
framework.

## Commands

```bash
dotnet build src/RecycleBinToolkit/RecycleBinToolkit/RecycleBinToolkit.csproj
dotnet build tests/RecycleBinToolkit.Tests/RecycleBinToolkit.Tests.csproj
dotnet run --project tests/RecycleBinToolkit.Tests -- --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --filter LongPathSuite
```

Build the test project after any change to `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` —
see the rule below. The suites change machine state: they write to `E:\RC\Test` (or the project root)
and they empty the Recycle Bin.

## README.md and ReadmeExamples.cs are one thing

`README.md` is the documentation. `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` holds the
same examples as compiling methods, which is what keeps a documented API shape from drifting into
something that no longer builds.

**A new example is added to both files in the same change, and a changed example is changed in both.**

- One example, one method, in both places. The method's body is the README code block, character for
  character, and the README sections follow the order of the methods in the file.
- A README block may prepend `using RecycleBinToolkit;` so a reader can paste it as a standalone
  snippet. Nothing else in a block may differ from the method body.
- Comments belong inside the code block, not only in the surrounding prose. A reader who copies the
  block gets the comment with it. Keep them to what the method names cannot say.
- The file compiles but is never run, so an example may hard-code paths and never touch the disk.
  Give each one a name that says what it demonstrates.
- The one sanctioned difference: the assertion example uses `Check.*` because the test project has no
  xunit, and README.md spells the same three lines `Assert.*`.

When a signature changes, both files change — the tables in README.md, the example in the README, and
the method in `ReadmeExamples.cs`. A README claim that no longer matches the code is a defect the same
as a failing test.

## Claims

Behaviour stated in README.md should be traceable to a measurement in
`docs/superpowers/specs/2026-10-06-recycle-bin-reliability-design.md`, and new behaviour should arrive
with a test that was seen to fail first.

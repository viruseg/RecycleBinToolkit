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
dotnet run --project tests/RecycleBinToolkit.Tests -- --load
dotnet run --project tests/RecycleBinToolkit.Tests -- --manual "F:\" --manual "\\server\share\"
```

Build the test project after any change to `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` —
see the rule below. The suites change machine state: they write to `E:\RC\Test` (or the project root)
and they empty the Recycle Bin. The manual group goes further and writes to — and deletes from — whatever
path `--manual` names.

## Test groups

The harness has three disjoint groups. `--all` and `--filter <substring>` select the regular group,
`--load` selects the load group, and no argument other than `--load` runs a load case. A `--filter` that
names only load cases fails with a message naming them and telling the reader to pass `--load`, so the
flag cannot be worked around by accident.

`--manual <root>` selects the manual group, is repeatable, and runs every manual case once per root with
that root handed to the case through `TestContext.ManualRoot`. The paths are the caller's: nothing
hard-codes a root, and a manual case creates everything it needs in a subdirectory of its own and removes
only that directory, by full path. `--all` names the deferred manual cases in its count line, and a
`--filter` that names only manual cases tells the reader to pass `--manual`, exactly as it does for load
cases.

One run selects one group. `--manual <root> --load` selects the manual group and prints that it ignored
`--load`, because a flag that vanishes quietly is the failure the deferral lines exist to prevent. A
`--manual` with no path after it, or with a flag after it, is a usage error rather than a root: the
argument the cases delete against is the one thing that must not be guessed. Write a share in backslashes
— `--manual "\\server\share\"` — because a forward-slash share is not recognised as a volume root and every
case would go red on it.

**The manual group needs real hardware, and it destroys files on the path you name.** A removable drive
with no `$RECYCLE.BIN` and a network share are the two volumes the ordinary run cannot produce, which is
why these cases exist at all — a `subst` mapping stands in for one on any machine, and a real volume is
what tells you the Shell's own code on that hardware. A manual case goes **red** when the root turns out
not to be a volume with no Recycle Bin — a healthy one, one whose Shell *does* answer a question about its
bin, or a letter nothing is mounted on — naming the root and its `Reason`: a caller who passed `E:\` asked
a question these cases cannot answer, and a silent pass would read afterwards as coverage nobody provided.

**A manual case is never the only coverage of a claim either.** The rule the load group follows applies
unchanged: an ordinary case must already assert the same thing without the hardware, and the repetition
the hardware adds is the only thing allowed to stay behind the flag.

| Manual case | Covered without the hardware by |
| --- | --- |
| `VolumeSuite.AVolumeWithoutARecycleBinIsRefused` | `FacadeSuite.RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff` |
| `VolumeSuite.AVolumeWithoutARecycleBinCanBeDeletedOnRequest` | `FacadeSuite.DeletePermanentlyRemovesTheFileAndNamesIt` |
| `VolumeSuite.AStatusAndAnEmptyReadTheSameRootTheSameWay` | `EmptySuite.AForwardSlashSpellingIsTheVolumeToEmpty`, `EmptySuite.AnExtendedLengthRootIsTheVolumeToEmpty` |

## Load tests

**A load case is the one that puts real load on the CPU or the disk, and nothing else qualifies.** Volume,
repetition, or a bound measured in elapsed time. The number of files it touches is not the test: ten files
of ten bytes are not a load case, and neither is a single 10 MB file, and neither is a batch of five small
files recycled in one call. Ask what the machine is doing — `StressSuite` regenerates a 94-file fixture set
ten times and moves 204 files through the Shell, which is where the disk gets worked hard enough to notice.
A batch over the whole fixture set is a load case on its own; the fixture set is 94 files of 64 KiB, so
creating and moving all of them is 5.75 MiB of writing for one call that still finishes in about a second.

**Mark the case where it is declared**, as the third argument of `new TestCase(...)`:
`new TestCase("Suite." + nameof(Case), Case, TestKind.Load)`. A load case stays in the suite that owns the
behaviour; the grouping belongs to the runner, not to the directory layout.

**A load case is never the only coverage of a claim.** Before marking one, confirm an ordinary case already
asserts the same thing without the load, and write that ordinary case if none does. The split exists so the
default run still proves the library works; a claim only `--load` checks is a claim the default run does not
make. What is allowed to stay behind the flag is the repetition itself — "this holds across ten cycles" is
what a load case is for, and no ordinary case can restate it.

The four load cases are all covered this way, and the ordinary cases that carry the claims are the reason
they are allowed to sit behind a flag:

| Load case | Covered without the load by |
| --- | --- |
| `StressSuite.TenCopyRecycleCyclesAlwaysSucceed` | `WorkflowSuite.FiveTouchedFilesAreAllRecycled`, `EmptySuite.EmptyReportsWhatItFreed` |
| `StressSuite.RecycledBytesReachTheBin` | `FacadeSuite.LargeFileIsRecycled` |
| `FacadeSuite.BatchOfGeneratedFilesIsRecycled` | `WorkflowSuite.FiveTouchedFilesAreAllRecycled`, `EmptySuite.EmptyReportsWhatItFreed` |
| `WorkflowSuite.ReadPartialAndAppendThenRecycle` | `WorkflowSuite.FiveTouchedFilesAreAllRecycled` |

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

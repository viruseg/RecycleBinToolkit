# Working in this repository

Windows-only, .NET 10, no external NuGet packages beyond `Microsoft.Windows.CsWin32` (a build-time
source generator). The test project is a console application with a hand-rolled harness, not a test
framework.

## Commands

```bash
dotnet build src/RecycleBinToolkit/RecycleBinToolkit/RecycleBinToolkit.csproj
dotnet build tests/RecycleBinToolkit.Tests/RecycleBinToolkit.Tests.csproj
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --all
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --filter LongPathSuite
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --load
dotnet run --project tests/RecycleBinToolkit.Tests -- --scratch D:\ --manual "F:\" --manual "\\server\share\"
```

Build the test project after any change to `tests/RecycleBinToolkit.Tests/Docs/ReadmeExamples.cs` —
see the rule below. The suites change machine state: they write into a directory they create inside the
volume `--scratch` names, and they empty the Recycle Bin. The manual group goes further and writes to —
and deletes from — whatever path `--manual` names.

## --scratch, and asking which volume

`--scratch <root>` is required, and every run needs one. It names the **root of a volume** — `D:\`, or
`\\server\share\`, or the forward-slash spelling `//server/share` for a command line that cannot carry two
leading backslashes. A directory inside a volume is refused, as is a bare drive letter: both resolve
against something other than the volume they name. Without the flag the runner runs nothing and exits 2,
because the suite writes files and deletes directories and neither is worth guessing at.

**Ask which volume to test on. Do not pick one.** The flag has no default and the repository's own location
is not one either. A run whose disk was chosen for it put its writes and its recursive deletes somewhere
the caller did not say, which is the one thing this flag exists to prevent — so the question is worth
asking even when the answer looks obvious, and it is asked before the run, not after it fails.

Inside the volume root the run claims `D:\{guid}` and takes `scratch` and `long` beneath it. The name is
generated and checked against what is already there, because `Directory.CreateDirectory` reports success
for a directory somebody else already had — a fixed name would let a case write into it and then delete it.
Removal is of the claimed path only, at the end of the run. No test path is derived from the repository,
so no test writes inside the working copy.

## Test groups

The harness has three disjoint groups. `--all` and `--filter <substring>` select the regular group,
`--load` selects the load group, and no argument other than `--load` runs a load case. A `--filter` that
names only load cases fails with a message naming them and telling the reader to pass `--load`, so the
flag cannot be worked around by accident.

`--manual <root>` selects the manual group, is repeatable, and runs every manual case once per root with
that root handed to the case through `TestContext.ManualRoot`. The paths are the caller's: nothing
hard-codes a root, and a manual case claims a directory of its own inside the one you name — a generated
name that was checked, because the case's cleanup is a recursive delete and a name taken from a leftover of
an earlier run would be a directory it writes into and then removes. Only the claimed directory is removed,
by full path. `--all` names the deferred manual cases in its count line, and a `--filter` that names only
manual cases tells the reader to pass `--manual`, exactly as it does for load cases.

One run selects one group. `--manual <root> --load` selects the manual group and prints that it ignored
`--load`, because a flag that vanishes quietly is the failure the deferral lines exist to prevent. A
`--manual` with no path after it, or with a flag after it, is a usage error rather than a root: the
argument the cases delete against is the one thing that must not be guessed.

**A share has to survive the command line, and the documented spelling does not everywhere.**
`--manual "\\server\share\"` arrives intact in PowerShell and `cmd.exe`, which treat backslashes literally.
Anything that reads `\\` as an escape delivers `\server\share\` instead, whose `Path.GetPathRoot` is `\` —
a path on the *current drive*, so the directory the cases create lands wherever the process happens to be.
That has already cost this repository one. `--manual "//server/share/"` is the spelling to use from such a
shell: the forward-slash form reaches the runner intact, and `VolumeRoot.Normalize` converts it, because the
library deliberately refuses it (`RecycleBinVolumeState.RootForm` only reduces backslashes) and the
conversion belongs where the command line is read. The same applies to `--scratch`. A root that is not a
volume root at all is refused by `VolumeRoot.Normalize`, before anything is created on it.

**The manual group needs real hardware, and it destroys files on the path you name.** A removable drive
with no `$RECYCLE.BIN` and a network share are the two volumes the ordinary run cannot produce, which is
why these cases exist at all — a `subst` mapping stands in for one on any machine, and a real volume is
what tells you the Shell's own code on that hardware. A manual case goes **red** when the root turns out
not to be a volume with no Recycle Bin — a healthy one, one whose Shell *does* answer a question about its
bin, or a letter nothing is mounted on — naming the root and its `Reason`: a caller who passed a healthy
volume asked a question these cases cannot answer, and a silent pass would read afterwards as coverage
nobody provided.

**A manual case is never the only coverage of a claim either.** The rule the load group follows applies
unchanged: an ordinary case must already assert the same thing without the hardware, and the repetition
the hardware adds is the only thing allowed to stay behind the flag.

| Manual case | Covered without the hardware by |
| --- | --- |
| `VolumeSuite.AVolumeWithoutARecycleBinIsRefused` | `FacadeSuite.RefusedPathIsLeftOnDiskWhenThrowOnErrorIsOff` |
| `VolumeSuite.AVolumeWithoutARecycleBinCanBeDeletedOnRequest` | `FacadeSuite.DeletePermanentlyRemovesTheFileAndNamesIt` |
| `VolumeSuite.AStatusAndAnEmptyReadTheSameRootTheSameWay` | `EmptySuite.AForwardSlashSpellingIsTheVolumeToEmpty`, `EmptySuite.AnExtendedLengthRootIsTheVolumeToEmpty` |

The group is three cases. With four load cases that leaves 144 of the 151 registered cases in the default
run; `--all` prints all three numbers, so a count in a document that disagrees with it is the document
being wrong.

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
- The one sanctioned difference is spelled in **both** files rather than kept between them: the assertion
  example reads `Check.*` in `README.md` and in `ReadmeExamples.cs`, and both carry the note that in a
  project with xunit the same three lines read `Assert.True`, `Assert.Equal` and `Assert.False`. Never
  "fix" one file to `Assert.*` to match a rule that says the README spells it that way — it does not.

When a signature changes, both files change — the tables in README.md, the example in the README, and
the method in `ReadmeExamples.cs`. A README claim that no longer matches the code is a defect the same
as a failing test.

## Claims

Behaviour stated in README.md should be traceable to a measurement in
`docs/superpowers/specs/2026-10-06-recycle-bin-reliability-design.md`, and new behaviour should arrive
with a test that was seen to fail first.
